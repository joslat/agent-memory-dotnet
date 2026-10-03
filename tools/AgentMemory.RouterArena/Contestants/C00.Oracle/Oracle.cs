using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 0 · The oracle: exactly what the labels need or accept, through exactly the doors the turn needs. Not a router: the
/// bound, the most any router could do.
/// </summary>
public sealed class Oracle : IContestant
{
    public string Family => "bound";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>();

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var ids = item.Needs.SelectMany(g => g).Concat(item.Acceptable).ToHashSet(StringComparer.Ordinal);
        var doors = Referee.DoorsNeeded(item, context.Data, context.Data.Matrix.DoorsLabelled);
        return new Decision(ids, doors, Doors.SearchesOf(doors), 0, "oracle");
    }
}
