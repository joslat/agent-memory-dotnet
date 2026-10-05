using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 7 · Nearest examples: the turn's embedding (recall computes it anyway) against labelled example turns; the doors the k
/// nearest needed vote, weighted by similarity; a door with at least <c>share</c> of the weight is opened.
/// </summary>
public sealed class NearestExamples(int k = 7, double share = 0.3, bool rewrite = false) : IContestant
{
    public string Family => "examples";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?> { ["k"] = k, ["share"] = share, ["rewrite"] = rewrite };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var neighbours = context.Neighbours(item, k, rewrite);
        var weight = neighbours.Sum(n => Math.Max(n.Similarity, 0));
        if (weight == 0) weight = 1;
        var votes = new Dictionary<Door, double>();
        foreach (var (similarity, example) in neighbours)
            foreach (var door in DoorsOf(example, context.Data))
                votes[door] = votes.GetValueOrDefault(door) + Math.Max(similarity, 0);
        var chosen = votes.Where(v => v.Value / weight >= share).Select(v => v.Key).ToList();
        return chosen.Count > 0 ? context.Open(chosen, rewrite) : Decision.Nothing();
    }

    /// <summary>The doors a labelled turn needs (its door labels; on a matrix without them, the doors holding what it needs).</summary>
    public static IReadOnlySet<Door> DoorsOf(MatrixItem item, ArenaData data) => Referee.DoorsNeeded(item, data, data.Matrix.DoorsLabelled);
}
