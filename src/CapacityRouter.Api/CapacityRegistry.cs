namespace CapacityRouter.Api;

/// <summary>
/// Thread-safe, in-memory store of capacities and allocations.
/// Every read-modify-write happens under one lock, so two concurrent route
/// requests can never reserve the same free units twice.
/// </summary>
public sealed class CapacityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CapacityNode> _capacities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Allocation> _allocations = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public CapacityRegistry(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Adds a capacity, or updates it if it already exists. Returns null if the
    /// new size would be smaller than the units already in use.</summary>
    public CapacitySnapshot? Register(string id, string region, int totalUnits)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_capacities.TryGetValue(id, out var existing))
            {
                if (totalUnits < existing.UsedUnits)
                {
                    return null;
                }

                existing.Region = region;
                existing.TotalUnits = totalUnits;
                existing.LastHeartbeat = now;
                existing.Healthy = true;
                return existing.ToSnapshot();
            }

            var node = new CapacityNode
            {
                Id = id,
                Region = region,
                TotalUnits = totalUnits,
                LastHeartbeat = now,
                Healthy = true
            };
            _capacities[id] = node;
            return node.ToSnapshot();
        }
    }

    public IReadOnlyList<CapacitySnapshot> List()
    {
        lock (_gate)
        {
            return _capacities.Values
                .Select(c => c.ToSnapshot())
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Records a heartbeat. Returns false if the capacity is unknown.
    /// <paramref name="recovered"/> is true when an unhealthy capacity comes back.</summary>
    public bool Heartbeat(string id, out bool recovered)
    {
        lock (_gate)
        {
            recovered = false;
            if (!_capacities.TryGetValue(id, out var node))
            {
                return false;
            }

            node.LastHeartbeat = _time.GetUtcNow();
            if (!node.Healthy)
            {
                node.Healthy = true;
                recovered = true;
            }
            return true;
        }
    }

    /// <summary>Marks capacities unhealthy when their last heartbeat is older than
    /// <paramref name="timeout"/>. Returns the ids that changed state.</summary>
    public IReadOnlyList<string> MarkStale(TimeSpan timeout)
    {
        lock (_gate)
        {
            var cutoff = _time.GetUtcNow() - timeout;
            var changed = new List<string>();
            foreach (var node in _capacities.Values)
            {
                if (node.Healthy && node.LastHeartbeat < cutoff)
                {
                    node.Healthy = false;
                    changed.Add(node.Id);
                }
            }
            return changed;
        }
    }

    /// <summary>Chooses a capacity and reserves units on it atomically.</summary>
    public RouteResult Route(RouteRequest request)
    {
        lock (_gate)
        {
            var snapshots = _capacities.Values.Select(c => c.ToSnapshot()).ToList();
            if (!snapshots.Any(c => c.Healthy))
            {
                return RouteResult.Fail(RouteFailure.NoHealthyCapacity);
            }

            var chosen = RoutingPolicy.Choose(snapshots, request.Units, request.PreferredRegion);
            if (chosen is null)
            {
                return RouteResult.Fail(RouteFailure.InsufficientCapacity);
            }

            var node = _capacities[chosen.Id];
            node.UsedUnits += request.Units;

            var allocation = new Allocation(
                Id: Guid.NewGuid().ToString("n"),
                WorkloadId: request.WorkloadId,
                CapacityId: node.Id,
                Region: node.Region,
                Units: request.Units,
                CreatedAt: _time.GetUtcNow());
            _allocations[allocation.Id] = allocation;
            return RouteResult.Ok(allocation);
        }
    }

    /// <summary>Frees the units held by an allocation. Returns false if it does not exist.</summary>
    public bool Release(string allocationId)
    {
        lock (_gate)
        {
            if (!_allocations.Remove(allocationId, out var allocation))
            {
                return false;
            }

            if (_capacities.TryGetValue(allocation.CapacityId, out var node))
            {
                node.UsedUnits = Math.Max(0, node.UsedUnits - allocation.Units);
            }
            return true;
        }
    }

    private sealed class CapacityNode
    {
        public required string Id { get; init; }
        public required string Region { get; set; }
        public int TotalUnits { get; set; }
        public int UsedUnits { get; set; }
        public DateTimeOffset LastHeartbeat { get; set; }
        public bool Healthy { get; set; }

        public CapacitySnapshot ToSnapshot() =>
            new(Id, Region, TotalUnits, UsedUnits, TotalUnits - UsedUnits, Healthy, LastHeartbeat);
    }
}
