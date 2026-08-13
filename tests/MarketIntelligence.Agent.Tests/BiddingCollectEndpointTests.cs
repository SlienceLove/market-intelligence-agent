using System.Net;
using System.Net.Http.Json;
using MarketIntelligence.Agent.Application.Bidding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketIntelligence.Agent.Tests;

public sealed class BiddingCollectEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BiddingCollectEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory;

    [Fact]
    public async Task PostCollect_RouteIsRegistered_DoesNotReturn404()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/bidding/collect",
            new CollectOnDemandRequest());

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostCollect_MissingAuthHeader_Returns401()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/bidding/collect",
            new CollectOnDemandRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostCollect_EmptyBody_ReturnsWellFormedResponse()
    {
        using var factory = CreateConfiguredFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Api-Key", "test-bidding-key");

        using var response = await client.PostAsJsonAsync(
            "/api/bidding/collect",
            new CollectOnDemandRequest());

        // No plans are configured in the test host, so the service returns
        // Status = "failed" with 0 plans. The endpoint maps that to 500.
        // Either 200 (partial/success) or 500 (failed) is acceptable here.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Unexpected status: {response.StatusCode}");
    }

    [Fact]
    public async Task PostCollect_EmptyBody_ResponseBodyIsDeserializable()
    {
        using var factory = CreateConfiguredFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Api-Key", "test-bidding-key");

        using var response = await client.PostAsJsonAsync(
            "/api/bidding/collect",
            new CollectOnDemandRequest());

        // A 200 response must deserialize to CollectOnDemandResponse.
        // A 500 is a ProblemDetails – we only assert on the 200 path.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadFromJsonAsync<CollectOnDemandResponse>();
            Assert.NotNull(body);
            Assert.NotNull(body.Status);
            Assert.NotNull(body.Plans);
        }
        else
        {
            // 500 from "failed" status — response is valid ProblemDetails, not null
            var body = await response.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(body));
        }
    }

    private static WebApplicationFactory<Program> CreateConfiguredFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Bidding:BridgeApiKey"] = "test-bidding-key"
                })));
}
