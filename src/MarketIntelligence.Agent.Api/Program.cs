using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketIntelligence.Agent.Api;
using MarketIntelligence.Agent.Application;
using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Application.Images;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure;
using MarketIntelligence.Agent.Infrastructure.Bidding;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddBiddingCollectionInfrastructure();

// Development-only defaults keep the demo self-contained. Explicit plan-root
// or live-platform configuration takes precedence so development can also
// exercise the production wiring.
if (builder.Environment.IsDevelopment())
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["Bidding:PlanRoot"]))
    {
        var demoPlan = new ScheduledCollectionPlan
        {
            PlanId = "demo-plan-001",
            Name = "演示计划 - IT采购招标",
            Keywords = ["云计算", "软件采购", "信息化", "大数据"],
            NotificationChannel = ScheduledNotificationChannels.Webhook,
            ExecutionTimeUtc = new TimeOnly(0, 0), // always due (any time past midnight)
            LookbackDays = 7,
            MaxResults = 20,
            Enabled = true
        };
        builder.Services.AddSingleton<IScheduledCollectionPlanSource>(
            _ => new InMemoryScheduledCollectionPlanSource([demoPlan]));
    }

    if (!builder.Configuration
            .GetSection("Bidding:Collector:EnabledPlatforms")
            .GetChildren()
            .Any(child => !string.IsNullOrWhiteSpace(child.Value)))
    {
        builder.Services.AddSingleton<IBiddingNoticeCollector, DemoFixtureBiddingNoticeCollector>();
    }
}

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

app.MapPost("/api/bidding/collect", async Task<IResult> (
    HttpRequest httpRequest,
    IConfiguration configuration,
    CollectOnDemandRequest? request,
    OnDemandCollectionService service,
    CancellationToken cancellationToken) =>
{
    if (!ServiceAuthorization.IsAuthorized(httpRequest, configuration, "Bidding:BridgeApiKey"))
    {
        return Results.Unauthorized();
    }

    var result = await service.ExecuteAsync(request ?? new CollectOnDemandRequest(), cancellationToken);
    return result.Status == "failed"
        ? Results.Problem(detail: "Collection failed for all plans.", statusCode: 500)
        : Results.Ok(result);
});

app.Run();

public partial class Program { }
