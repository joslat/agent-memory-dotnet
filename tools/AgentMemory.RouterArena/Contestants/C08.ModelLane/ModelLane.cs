using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 8 · The model lane: a small model (GLM-5.3-Flash on Bitdeer, low reasoning effort) read the turn and named the doors
/// (or, asked before the doors, the kinds: facts, graph, preferences, messages, shared); its answers were recorded once and
/// are replayed here, free. The doors are opened as shipped; a failed call opens what the old method reads. Measured
/// cost: ~5 s a call, against ~36 ms for recall itself.
/// </summary>
public sealed class ModelLane(bool rewrite = false) : IContestant
{
    public string Family => "model";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>
    {
        ["model"] = "GLM-5.3-Flash", ["effort"] = "low", ["rewrite"] = rewrite,
    };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        if (!context.Data.ModelAnswers.TryGetValue(item.Id, out var answer) || answer is null)
            return Decision.Of(context.Source(rewrite, wide: false), Doors.Shipped, model: 1, lane: "model-failed");
        if (answer.Count == 0) return Decision.Nothing(model: 1, lane: "model");
        return context.Open(answer.SelectMany(Parse), rewrite, model: 1, lane: "model");
    }

    /// <summary>A door's name, or one of the kinds the lane named before the doors.</summary>
    internal static IEnumerable<Door> Parse(string name) => name switch
    {
        "facts" or "shared" => [Door.Semantic, Door.Derived],
        "graph" => [Door.EntityGraph],
        "preferences" => [Door.Preference],
        "messages" => [Door.Episodic],
        _ => [Doors.Parse(name)],
    };
}
