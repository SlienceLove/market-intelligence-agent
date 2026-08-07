using System.Security.Cryptography;
using System.Text;
using MarketIntelligence.Agent.Application.Images;
using MarketIntelligence.Agent.Application;
using MarketIntelligence.Agent.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
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
app.Run();

public partial class Program { }
