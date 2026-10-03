using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 6 · Door gates (the owner's gate, before the search): each door has its own gate, which opens when the turn's nearest
/// labelled examples needed that door with at least the door's own share of the weight. Only the open doors are searched,
/// as shipped. A module would bring its memory type and its gate together.
/// </summary>
public sealed class DoorGates(IReadOnlyDictionary<Door, double> thresholds, double defaultThreshold = 0.3, int k = 9, bool rewrite = false) : IContestant
{
    public IReadOnlyDictionary<Door, double> Thresholds { get; } = thresholds;

    public double DefaultThreshold { get; } = defaultThreshold;

    public string Family => "gates";

    public IReadOnlyDictionary<string, object?> Parameters
    {
        get
        {
            var p = new Dictionary<string, object?> { ["*"] = DefaultThreshold, ["k"] = k, ["rewrite"] = rewrite };
            foreach (var (door, value) in Thresholds) p[Doors.Name(door)] = value;
            return p;
        }
    }

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var neighbours = context.Neighbours(item, k, rewrite);
        var weight = neighbours.Sum(n => Math.Max(n.Similarity, 0));
        if (weight == 0) weight = 1;
        var open = new HashSet<Door>();
        foreach (var door in Doors.All)
        {
            var share = neighbours.Where(n => NearestExamples.DoorsOf(n.Example, context.Data).Contains(door)).Sum(n => Math.Max(n.Similarity, 0)) / weight;
            if (share >= Thresholds.GetValueOrDefault(door, DefaultThreshold)) open.Add(door);
        }
        return open.Count > 0 ? context.Open(open, rewrite) : Decision.Nothing();
    }
}
