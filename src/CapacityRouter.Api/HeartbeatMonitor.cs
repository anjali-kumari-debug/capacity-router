using Microsoft.Extensions.Options;

namespace CapacityRouter.Api;

/// <summary>
/// Background health check: every few seconds, marks capacities that stopped sending
/// heartbeats as unhealthy so the router stops sending new work to them.
/// One failed pass is logged and the loop keeps running.
/// </summary>
public sealed class HeartbeatMonitor : BackgroundService
{
    private readonly CapacityRegistry _registry;
    private readonly RouterMetrics _metrics;
    private readonly HeartbeatOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HeartbeatMonitor> _logger;

    public HeartbeatMonitor(
        CapacityRegistry registry,
        RouterMetrics metrics,
        IOptions<HeartbeatOptions> options,
        TimeProvider time,
        ILogger<HeartbeatMonitor> logger)
    {
        _registry = registry;
        _metrics = metrics;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var id in _registry.MarkStale(timeout))
                    {
                        _metrics.CapacityMarkedUnhealthy();
                        _logger.LogWarning(
                            "Capacity {CapacityId} missed heartbeats for {TimeoutSeconds}s and was marked unhealthy",
                            id, _options.TimeoutSeconds);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Heartbeat check failed; will retry on next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
