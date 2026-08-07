using System.Net.Http.Json;
using System.Text.Json;
using MarketIntelligence.Agent.Application.Images;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Images;

public sealed class ComfyUiImageGenerationService(
    HttpClient httpClient,
    IOptions<ComfyUiOptions> options,
    ILogger<ComfyUiImageGenerationService> logger) : IImageGenerationService
{
    private readonly ComfyUiOptions _options = options.Value;

    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(request);
        if (validation is not null)
        {
            return validation;
        }

        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return ImageGenerationResult.Failed(
                "comfyui_not_configured",
                "ComfyUI base URL is not configured.");
        }

        httpClient.BaseAddress = baseUri;
        var clientId = Guid.NewGuid().ToString("N");
        var workflow = BuildWorkflow(request);

        try
        {
            using var queueResponse = await httpClient.PostAsJsonAsync(
                "prompt",
                new { prompt = workflow, client_id = clientId },
                cancellationToken);

            if (!queueResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ComfyUI rejected image job with status {StatusCode}.",
                    (int)queueResponse.StatusCode);
                return ImageGenerationResult.Failed(
                    "comfyui_queue_failed",
                    "ComfyUI rejected the image job.");
            }

            using var queueDocument = await JsonDocument.ParseAsync(
                await queueResponse.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            if (!queueDocument.RootElement.TryGetProperty("prompt_id", out var promptIdElement) ||
                promptIdElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(promptIdElement.GetString()))
            {
                return ImageGenerationResult.Failed(
                    "comfyui_invalid_queue_response",
                    "ComfyUI returned no prompt identifier.");
            }

            var promptId = promptIdElement.GetString()!;
            return await WaitForImageAsync(promptId, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ImageGenerationResult.Failed(
                "comfyui_timeout",
                "ComfyUI image generation timed out.");
        }
        catch (HttpRequestException)
        {
            return ImageGenerationResult.Failed(
                "comfyui_unreachable",
                "ComfyUI is not reachable.");
        }
    }

    private async Task<ImageGenerationResult> WaitForImageAsync(
        string promptId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 900)));

        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_options.PollIntervalMilliseconds, 100, 5000));

        while (true)
        {
            using var historyResponse = await httpClient.GetAsync(
                $"history/{Uri.EscapeDataString(promptId)}",
                timeout.Token);

            if (historyResponse.IsSuccessStatusCode)
            {
                using var historyDocument = await JsonDocument.ParseAsync(
                    await historyResponse.Content.ReadAsStreamAsync(timeout.Token),
                    cancellationToken: timeout.Token);

                if (TryReadFailure(historyDocument.RootElement, promptId, out var failure))
                {
                    return failure;
                }

                if (TryReadImage(historyDocument.RootElement, promptId, out var image))
                {
                    return new ImageGenerationResult(
                        true,
                        promptId,
                        BuildAssetUrl(image.Filename, image.Subfolder, image.Type),
                        image.Filename,
                        null,
                        null);
                }
            }

            await Task.Delay(interval, timeout.Token);
        }
    }

    private string BuildAssetUrl(string filename, string subfolder, string type)
    {
        var builder = new UriBuilder(new Uri(httpClient.BaseAddress!, "view"));
        builder.Query = string.Join(
            "&",
            $"filename={Uri.EscapeDataString(filename)}",
            $"subfolder={Uri.EscapeDataString(subfolder)}",
            $"type={Uri.EscapeDataString(type)}");
        return builder.Uri.ToString();
    }

    private Dictionary<string, object> BuildWorkflow(ImageGenerationRequest request) =>
        new()
        {
            ["1"] = new
            {
                class_type = "CheckpointLoaderSimple",
                inputs = new { ckpt_name = _options.CheckpointName }
            },
            ["2"] = new
            {
                class_type = "CLIPTextEncode",
                inputs = new
                {
                    text = request.Prompt,
                    clip = new object[] { "1", 1 }
                }
            },
            ["3"] = new
            {
                class_type = "CLIPTextEncode",
                inputs = new
                {
                    text = string.IsNullOrWhiteSpace(request.NegativePrompt)
                        ? "text, watermark, logo, blurry, low quality, malformed"
                        : request.NegativePrompt,
                    clip = new object[] { "1", 1 }
                }
            },
            ["4"] = new
            {
                class_type = "EmptyLatentImage",
                inputs = new
                {
                    width = request.Width,
                    height = request.Height,
                    batch_size = 1
                }
            },
            ["5"] = new
            {
                class_type = "KSampler",
                inputs = new
                {
                    seed = request.Seed ?? Random.Shared.NextInt64(),
                    steps = request.Steps,
                    cfg = 7.0,
                    sampler_name = "euler",
                    scheduler = "normal",
                    denoise = 1.0,
                    model = new object[] { "1", 0 },
                    positive = new object[] { "2", 0 },
                    negative = new object[] { "3", 0 },
                    latent_image = new object[] { "4", 0 }
                }
            },
            ["6"] = new
            {
                class_type = "VAEDecode",
                inputs = new
                {
                    samples = new object[] { "5", 0 },
                    vae = new object[] { "1", 2 }
                }
            },
            ["7"] = new
            {
                class_type = "SaveImage",
                inputs = new
                {
                    filename_prefix = "market-intelligence",
                    images = new object[] { "6", 0 }
                }
            }
        };

    private static ImageGenerationResult? Validate(ImageGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 2000)
        {
            return ImageGenerationResult.Failed(
                "invalid_prompt",
                "Prompt must contain 1 to 2000 characters.");
        }

        if (request.Width is < 128 or > 768 || request.Width % 8 != 0 ||
            request.Height is < 128 or > 768 || request.Height % 8 != 0)
        {
            return ImageGenerationResult.Failed(
                "invalid_dimensions",
                "Image dimensions must be multiples of 8 between 128 and 768.");
        }

        if (request.Steps is < 1 or > 20)
        {
            return ImageGenerationResult.Failed(
                "invalid_steps",
                "Steps must be between 1 and 20.");
        }

        return null;
    }

    private static bool TryReadFailure(
        JsonElement history,
        string promptId,
        out ImageGenerationResult failure)
    {
        failure = default!;
        if (!history.TryGetProperty(promptId, out var record) ||
            !record.TryGetProperty("status", out var status) ||
            !status.TryGetProperty("status_str", out var statusString))
        {
            return false;
        }

        var value = statusString.GetString();
        if (value is not ("error" or "failed"))
        {
            return false;
        }

        failure = ImageGenerationResult.Failed(
            "comfyui_generation_failed",
            "ComfyUI failed to generate the image.");
        return true;
    }

    private static bool TryReadImage(
        JsonElement history,
        string promptId,
        out (string Filename, string Subfolder, string Type) image)
    {
        image = default;
        if (!history.TryGetProperty(promptId, out var record) ||
            !record.TryGetProperty("outputs", out var outputs))
        {
            return false;
        }

        foreach (var node in outputs.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("images", out var images))
            {
                continue;
            }

            foreach (var item in images.EnumerateArray())
            {
                if (!item.TryGetProperty("filename", out var filename) ||
                    filename.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                image = (
                    filename.GetString() ?? string.Empty,
                    item.TryGetProperty("subfolder", out var subfolder)
                        ? subfolder.GetString() ?? string.Empty
                        : string.Empty,
                    item.TryGetProperty("type", out var type)
                        ? type.GetString() ?? "output"
                        : "output");
                return !string.IsNullOrWhiteSpace(image.Filename);
            }
        }

        return false;
    }
}
