using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 1 · The old method, the champion: no router. Every searching door on every turn (the conversational preset: the
/// profile and the last messages, facts with the derived ones, the graph, preferences, messages, traces), each kept above
/// a similarity floor and up to its cap, with fan-out on; no reading door (valid time is ignored, nothing is read as of a
/// date, prospective firing and legible forgetting are off). <paramref name="setting"/> picks a recorded re-tuning of it
/// ("0.65x2": floor 0.65, caps doubled): the real recall at that setting, not a simulation. <paramref name="rewrite"/>
/// recalls a follow-up together with the turn before (contestant 10 crossed in).
/// </summary>
public sealed class OldMethod(string? setting = null, bool rewrite = false) : IContestant
{
    public string Family => "champion";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>
    {
        ["setting"] = setting ?? "shipped (floor 0.70, caps as shipped, fan-out on)", ["rewrite"] = rewrite,
    };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        if (setting is null) return Decision.Of(context.Source(rewrite, wide: false), Doors.Shipped);
        var variants = context.Record.Variants ?? throw new InvalidOperationException("the recording has no re-tuned settings of the old method");
        return Decision.Of(context.Recorded($"variant:{setting}", variants[setting]), Doors.Shipped);
    }
}

/// <summary>Every door wide open (every searching door up to 50, no floor; every reading door): a bound, the most the search itself can find.</summary>
public sealed class WideOpen : IContestant
{
    public string Family => "bound";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?> { ["cap"] = 50, ["floor"] = 0 };

    public Decision Decide(MatrixItem item, TurnContext context) =>
        Decision.Of(context.Source(false, wide: true).Concat(Doors.Reading.SelectMany(context.Reading)), Doors.All) with { Wide = true };
}
