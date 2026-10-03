using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 11 · Soft budgets as one pool: every searching door searched wide open; the best k items across all of them go in,
/// whatever their door. A door is open when something of it went in.
/// </summary>
public sealed class OnePool(int k, double floor = 0, bool rewrite = false) : IContestant
{
    public string Family => "pool";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?> { ["k"] = k, ["floor"] = floor, ["rewrite"] = rewrite };

    private static readonly int Searches = Doors.SearchesOf(Doors.Searching.ToHashSet());

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var kept = context.Source(rewrite, wide: true).Where(c => c.Score is not null && c.Score >= floor)
            .OrderByDescending(c => c.Score!.Value).Take(k).ToList();
        return new Decision(kept.Select(c => c.Id).ToHashSet(StringComparer.Ordinal), kept.Select(c => c.Door).ToHashSet(), Searches)
        {
            Wide = true, SearchedDoors = Doors.Searching.ToHashSet(),
        };
    }
}
