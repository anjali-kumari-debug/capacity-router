namespace CapacityRouter.Api;

/// <summary>
/// Pure routing decision, kept separate from state so it is easy to unit test.
/// Rules:
///   1. Only healthy capacities with enough free units are eligible.
///   2. If a preferred region is given and has an eligible capacity, stay in that region.
///      Otherwise fall back to any region (availability over locality).
///   3. Pick the capacity with the most free units (least-loaded), ties broken by Id
///      so the decision is deterministic.
/// </summary>
public static class RoutingPolicy
{
    public static CapacitySnapshot? Choose(IEnumerable<CapacitySnapshot> capacities, int units, string? preferredRegion)
    {
        var eligible = capacities
            .Where(c => c.Healthy && c.FreeUnits >= units)
            .ToList();

        if (eligible.Count == 0)
        {
            return null;
        }

        var pool = eligible;
        if (!string.IsNullOrWhiteSpace(preferredRegion))
        {
            var inRegion = eligible
                .Where(c => string.Equals(c.Region, preferredRegion, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (inRegion.Count > 0)
            {
                pool = inRegion;
            }
        }

        return pool
            .OrderByDescending(c => c.FreeUnits)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .First();
    }
}
