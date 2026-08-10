using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketIntelligence.Agent.Application.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketIntelligence.Agent.Tests;

public sealed class MediaJobEndpointTests
{
    [Fact]
    public async Task Submit_requires_service_key()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/media/jobs",
            ValidRequest("api-auth"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authorized_submit_returns_async_acceptance_and_location()
    {
        using var factory = CreateConfiguredFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Api-Key", "test-key");

        using var response = await client.PostAsJsonAsync(
            "/api/media/jobs",
            ValidRequest("api-submit"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/api/media/jobs/api-submit", response.Headers.Location?.OriginalString);
        var result = await response.Content.ReadFromJsonAsync<MediaJobResult>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            });
        Assert.Equal(MediaJobStatus.Accepted, result?.Status);
    }

    [Fact]
    public async Task Authorized_query_and_cancel_return_not_found_for_unknown_job()
    {
        using var factory = CreateConfiguredFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Api-Key", "test-key");

        var query = await client.GetAsync("/api/media/jobs/missing");
        var cancel = await client.PostAsync("/api/media/jobs/missing/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, query.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    private static MediaJobRequest ValidRequest(string jobId) => new(
        jobId,
        MediaJobKind.Collection,
        [new MediaAssetReference("https://allowed.example/video", "text/uri-list")],
        IdempotencyKey: $"idem-{jobId}");

    private static WebApplicationFactory<Program> CreateConfiguredFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Media:BridgeApiKey"] = "test-key",
                    ["Media:Collector:Enabled"] = "false",
                    ["Media:Asr:Enabled"] = "false"
                })));
}
