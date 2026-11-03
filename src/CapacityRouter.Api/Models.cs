namespace CapacityRouter.Api;

/// <summary>A capacity (a pool of compute units in one region) registering with the router.</summary>
public sealed record RegisterCapacityRequest(string Id, string Region, int TotalUnits)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(Id)) errors[nameof(Id)] = ["Id is required."];
        if (string.IsNullOrWhiteSpace(Region)) errors[nameof(Region)] = ["Region is required."];
        if (TotalUnits <= 0) errors[nameof(TotalUnits)] = ["TotalUnits must be greater than 0."];
        return errors;
    }
}

/// <summary>A workload asking for a number of capacity units, optionally in a preferred region.</summary>
public sealed record RouteRequest(string WorkloadId, int Units, string? PreferredRegion = null)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(WorkloadId)) errors[nameof(WorkloadId)] = ["WorkloadId is required."];
        if (Units <= 0) errors[nameof(Units)] = ["Units must be greater than 0."];
        return errors;
    }
}

/// <summary>Read-only view of a capacity, safe to return from the API.</summary>
public sealed record CapacitySnapshot(
    string Id,
    string Region,
    int TotalUnits,
    int UsedUnits,
    int FreeUnits,
    bool Healthy,
    DateTimeOffset LastHeartbeat);

/// <summary>Units reserved on one capacity for one workload.</summary>
public sealed record Allocation(
    string Id,
    string WorkloadId,
    string CapacityId,
    string Region,
    int Units,
    DateTimeOffset CreatedAt);

public enum RouteFailure
{
    NoHealthyCapacity,
    InsufficientCapacity
}

public sealed record RouteResult(Allocation? Allocation, RouteFailure? Failure)
{
    public bool Succeeded => Allocation is not null;
    public static RouteResult Ok(Allocation allocation) => new(allocation, null);
    public static RouteResult Fail(RouteFailure failure) => new(null, failure);
}
