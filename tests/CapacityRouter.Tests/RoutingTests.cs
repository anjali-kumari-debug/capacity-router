using CapacityRouter.Api;
using Xunit;

namespace CapacityRouter.Tests;

/// <summary>Clock the tests control, so heartbeat timeouts can be tested without waiting.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public class RoutingPolicyTests
{
    private static CapacitySnapshot Cap(string id, string region, int total, int used, bool healthy = true) =>
        new(id, region, total, used, total - used, healthy, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Picks_least_loaded_capacity()
    {
        var caps = new[] { Cap("a", "eastus", 10, 8), Cap("b", "eastus", 10, 2), Cap("c", "eastus", 10, 5) };
        Assert.Equal("b", RoutingPolicy.Choose(caps, 1, null)!.Id);
    }

    [Fact]
    public void Prefers_requested_region_even_if_another_has_more_room()
    {
        var caps = new[] { Cap("a", "westeurope", 100, 0), Cap("b", "centralindia", 10, 5) };
        Assert.Equal("b", RoutingPolicy.Choose(caps, 2, "centralindia")!.Id);
    }

    [Fact]
    public void Falls_back_to_other_region_when_preferred_region_is_full()
    {
        var caps = new[] { Cap("a", "centralindia", 10, 10), Cap("b", "westeurope", 10, 0) };
        Assert.Equal("b", RoutingPolicy.Choose(caps, 2, "centralindia")!.Id);
    }

    [Fact]
    public void Skips_unhealthy_capacities()
    {
        var caps = new[] { Cap("a", "eastus", 100, 0, healthy: false), Cap("b", "eastus", 10, 0) };
        Assert.Equal("b", RoutingPolicy.Choose(caps, 1, null)!.Id);
    }

    [Fact]
    public void Returns_null_when_nothing_fits()
    {
        var caps = new[] { Cap("a", "eastus", 10, 9) };
        Assert.Null(RoutingPolicy.Choose(caps, 2, null));
    }

    [Fact]
    public void Breaks_ties_deterministically_by_id()
    {
        var caps = new[] { Cap("z", "eastus", 10, 0), Cap("m", "eastus", 10, 0) };
        Assert.Equal("m", RoutingPolicy.Choose(caps, 1, null)!.Id);
    }
}

public class CapacityRegistryTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly CapacityRegistry _registry;

    public CapacityRegistryTests() => _registry = new CapacityRegistry(_time);

    [Fact]
    public void Route_reserves_units_and_release_frees_them()
    {
        _registry.Register("cap-1", "centralindia", 10);

        var result = _registry.Route(new RouteRequest("job-1", 4));
        Assert.True(result.Succeeded);
        Assert.Equal(6, _registry.List().Single().FreeUnits);

        Assert.True(_registry.Release(result.Allocation!.Id));
        Assert.Equal(10, _registry.List().Single().FreeUnits);
    }

    [Fact]
    public void Release_of_unknown_allocation_returns_false()
    {
        Assert.False(_registry.Release("does-not-exist"));
    }

    [Fact]
    public void Route_fails_with_no_healthy_capacity_when_registry_is_empty()
    {
        var result = _registry.Route(new RouteRequest("job-1", 1));
        Assert.Equal(RouteFailure.NoHealthyCapacity, result.Failure);
    }

    [Fact]
    public void Route_fails_with_insufficient_capacity_when_request_is_too_big()
    {
        _registry.Register("cap-1", "centralindia", 5);
        var result = _registry.Route(new RouteRequest("job-1", 6));
        Assert.Equal(RouteFailure.InsufficientCapacity, result.Failure);
    }

    [Fact]
    public void Missed_heartbeats_mark_capacity_unhealthy_and_heartbeat_recovers_it()
    {
        _registry.Register("cap-1", "centralindia", 10);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(new[] { "cap-1" }, _registry.MarkStale(TimeSpan.FromSeconds(30)));
        Assert.False(_registry.List().Single().Healthy);
        Assert.Equal(RouteFailure.NoHealthyCapacity, _registry.Route(new RouteRequest("job-1", 1)).Failure);

        Assert.True(_registry.Heartbeat("cap-1", out var recovered));
        Assert.True(recovered);
        Assert.True(_registry.Route(new RouteRequest("job-1", 1)).Succeeded);
    }

    [Fact]
    public void Capacity_with_recent_heartbeat_stays_healthy()
    {
        _registry.Register("cap-1", "centralindia", 10);
        _time.Advance(TimeSpan.FromSeconds(20));
        _registry.Heartbeat("cap-1", out _);
        _time.Advance(TimeSpan.FromSeconds(20));

        Assert.Empty(_registry.MarkStale(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Cannot_shrink_capacity_below_units_in_use()
    {
        _registry.Register("cap-1", "centralindia", 10);
        _registry.Route(new RouteRequest("job-1", 8));

        Assert.Null(_registry.Register("cap-1", "centralindia", 5));
        Assert.NotNull(_registry.Register("cap-1", "centralindia", 8));
    }

    [Fact]
    public async Task Concurrent_requests_never_overbook_a_capacity()
    {
        _registry.Register("cap-1", "centralindia", 50);

        var results = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(i => Task.Run(() => _registry.Route(new RouteRequest($"job-{i}", 1)))));

        Assert.Equal(50, results.Count(r => r.Succeeded));
        Assert.Equal(0, _registry.List().Single().FreeUnits);
    }
}
