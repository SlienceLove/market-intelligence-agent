using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketIntelligence.Agent.Api;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Application.Images;
using MarketIntelligence.Agent.Application;
using MarketIntelligence.Agent.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapPost("/api/image/generate", async Task<IResult> (
    HttpRequest httpRequest,
    IConfiguration configuration,
    IImageGenerationService generator,
    ImageGenerationRequest request,
    CancellationToken cancellationToken) =>
{
    var expectedKey = configuration["ComfyUi:BridgeApiKey"];
    var suppliedKey = httpRequest.Headers["X-Agent-Api-Key"].ToString();
    if (string.IsNullOrWhiteSpace(expectedKey) ||
        string.IsNullOrWhiteSpace(suppliedKey) ||
        !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedKey),
            Encoding.UTF8.GetBytes(suppliedKey)))
    {
        return Results.Unauthorized();
    }

    var result = await generator.GenerateAsync(request, cancellationToken);
    return result.Succeeded
        ? Results.Ok(result)
        : Results.Problem(
            result.FailureMessage,
            statusCode: result.FailureCode == "invalid_prompt" ||
                        result.FailureCode == "invalid_dimensions" ||
                        result.FailureCode == "invalid_steps"
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status502BadGateway,
            title: result.FailureCode);
});

app.MapPost("/api/media/jobs", async Task<IResult> (
    HttpRequest httpRequest,
    IConfiguration configuration,
    IMediaJobCoordinator coordinator,
    MediaJobRequest request,
    CancellationToken cancellationToken) =>
{
    if (!ServiceAuthorization.IsAuthorized(httpRequest, configuration, "Media:BridgeApiKey"))
    {
        return Results.Unauthorized();
    }

    var result = await coordinator.SubmitAsync(request, cancellationToken);
    if (result.Status == MediaJobStatus.Failed)
    {
        var statusCode = result.ErrorCategory switch
        {
            MediaFailureCategory.Validation or MediaFailureCategory.Unsupported => StatusCodes.Status400BadRequest,
            MediaFailureCategory.Conflict => StatusCodes.Status409Conflict,
            MediaFailureCategory.ProviderUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity
        };

        return Results.Problem(
            result.SafeFailureMessage,
            statusCode: statusCode,
            title: result.FailureCode);
    }

    return Results.Accepted($"/api/media/jobs/{result.JobId}", result);
});

app.MapGet("/api/media/jobs/{jobId}", (
    HttpRequest httpRequest,
    IConfiguration configuration,
    IMediaJobCoordinator coordinator,
    string jobId) =>
{
    if (!ServiceAuthorization.IsAuthorized(httpRequest, configuration, "Media:BridgeApiKey"))
    {
        return Results.Unauthorized();
    }

    var result = coordinator.Get(jobId);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/media/jobs/{jobId}/cancel", (
    HttpRequest httpRequest,
    IConfiguration configuration,
    IMediaJobCoordinator coordinator,
    string jobId) =>
{
    if (!ServiceAuthorization.IsAuthorized(httpRequest, configuration, "Media:BridgeApiKey"))
    {
        return Results.Unauthorized();
    }

    return coordinator.Cancel(jobId)
        ? Results.Ok(coordinator.Get(jobId))
        : Results.NotFound();
});

app.Run();

public partial class Program { }
