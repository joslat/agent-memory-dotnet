using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// A cross: each door decided by the contestant that judges it best. JEV before the search reads the time in the question
/// (now, then, due); JEV's gates after the search read the memories themselves (what a fact, a taste or a procedure says).
/// A door opens when its own judge opens it; what the open doors let in is what the shipped recall keeps of them. The
/// judges run one after the other (the first before the search, the second after it), so their times add up.
/// </summary>
public sealed class DoorMix(IContestant first, string firstName, IReadOnlySet<Door> firstDoors, IContestant rest, string restName) : IContestant
{
    public string Family => "cross";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>
    {
        [firstName] = string.Join(", ", firstDoors.Select(Doors.Name)), [restName] = "every other door",
    };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var a = first.Decide(item, context);
        var b = rest.Decide(item, context);
        var open = a.Opened.Where(firstDoors.Contains).Concat(b.Opened.Where(d => !firstDoors.Contains(d))).ToHashSet();
        var decision = context.Open(open, model: a.ModelCalls + b.ModelCalls, lane: $"{a.Lane}+{b.Lane}");
        return decision with
        {
            Searches = Math.Max(a.Searches, Math.Max(b.Searches, decision.Searches)),
            ModelSeconds = (a.ModelSeconds ?? a.ModelCalls * Referee.ModelSeconds) + (b.ModelSeconds ?? b.ModelCalls * Referee.ModelSeconds),
            Wide = a.Wide || b.Wide,
            SearchedDoors = (a.SearchedDoors ?? a.Opened).Concat(b.SearchedDoors ?? b.Opened).ToHashSet(),
        };
    }
}
