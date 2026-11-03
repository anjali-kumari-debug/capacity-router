using System.Net;
using System.Net.Http.Json;
using CapacityRouter.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapacityRouter.Tests;

/// <summary>End-to-end tests: start the real app in memory and call it over HTTP.</summary>
public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_route_and_release_round_trip()
    {
        var client = _factory.CreateClient();
        var capacityId = $"cap-{Guid.NewGuid():n}";

        var register = await client.PostAsJsonAsync("/capacities",
            new RegisterCapacityRequest(capacityId, "test-region-roundtrip", 8));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var route = await client.PostAsJsonAsync("/route",
            new RouteRequest("job-1", 3, "test-region-roundtrip"));
        Assert.Equal(HttpStatusCode.OK, route.StatusCode);
        var allocation = await route.Content.ReadFromJsonAsync<Allocation>();
        Assert.Equal(capacityId, allocation!.CapacityId);

        var release = await client.DeleteAsync($"/allocations/{allocation.Id}");
        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);

        var releaseAgain = await client.DeleteAsync($"/allocations/{allocation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, releaseAgain.StatusCode);
    }

    [Fact]
    public async Task Invalid_route_request_returns_400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/route", new RouteRequest("", 0));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_larger_than_any_capacity_returns_503()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/capacities",
            new RegisterCapacityRequest($"cap-{Guid.NewGuid():n}", "test-region-small", 2));

        var response = await client.PostAsJsonAsync("/route", new RouteRequest("big-job", 1_000_000));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_for_unknown_capacity_returns_404()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/capacities/unknown/heartbeat", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
