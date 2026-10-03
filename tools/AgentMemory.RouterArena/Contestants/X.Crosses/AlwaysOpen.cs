using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// A cross: some doors open on every turn (the broad ones, which no router tells apart well), and another contestant
/// decides the rest. Crossed with <see cref="OpenThenGate"/>, the always-open doors are still cut by their gates after the
/// search: a specialist decides which special memory a turn needs, the gates decide how much of the broad memory.
/// </summary>
public sealed class AlwaysOpen(IContestant opener, string openerName, params Door[] always) : IContestant
{
    public string Family => "cross";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>
    {
        ["opener"] = openerName, ["always"] = string.Join(", ", always.Select(Doors.Name)),
    };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var first = opener.Decide(item, context);
        return context.Open(first.Opened.Concat(always), model: first.ModelCalls, lane: first.Lane) with { ModelSeconds = first.ModelSeconds };
    }
}
