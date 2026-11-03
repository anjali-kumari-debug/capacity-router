using System.Diagnostics.Metrics;

namespace CapacityRouter.Api;

/// <summary>Custom metrics. Exported to Application Insights (customMetrics table)
/// when APPLICATIONINSIGHTS_CONNECTION_STRING is set.</summary>
public sealed class RouterMetrics
{
    public const string MeterName = "CapacityRouter";

    private readonly Counter<long> _routeRequests;
    private readonly Counter<long> _capacityMarkedUnhealthy;

    public RouterMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _routeRequests = meter.CreateCounter<long>(
            "router.route_requests", description: "Route requests by outcome");
        _capacityMarkedUnhealthy = meter.CreateCounter<long>(
            "router.capacity_marked_unhealthy", description: "Capacities that missed heartbeats");
    }

    public void RouteRequested(string outcome) =>
        _routeRequests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void CapacityMarkedUnhealthy() => _capacityMarkedUnhealthy.Add(1);
}

public sealed class HeartbeatOptions
{
    public const string Section = "Heartbeat";

    /// <summary>A capacity with no heartbeat for this long is marked unhealthy.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>How often the monitor checks for stale capacities.</summary>
    public int CheckIntervalSeconds { get; set; } = 10;
}
