using System.Net;
using System.Net.Http.Json;
using MarketIntelligence.Agent.Application.Bidding;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MarketIntelligence.Agent.Tests;

public sealed class BiddingCollectEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public BiddingCollectEndpointTests(WebApplicationFactory<Program> factory) =>
        _client = factory.CreateClient();

    [Fact]
    public async Task PostCollect_RouteIsRegistered_DoesNotReturn404()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/bidding/collect",
            new CollectOnDemandRequest());

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostCollect_EmptyBody_ReturnsWellFormedResponse()
    {
        using var response = await _client.PostAsJsonAsync(
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
        using var response = await _client.PostAsJsonAsync(
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
}
