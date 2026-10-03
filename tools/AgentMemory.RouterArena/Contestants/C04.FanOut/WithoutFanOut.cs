using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 4 · The adaptive router (fan-out), measured the only fair way: the conversational preset already ships it inside the
/// old method, so its contribution is the old method minus this — the same recall with fan-out off. Its witness (did the
/// gate fire, which rule, which legs, any void) is read from the recording by <see cref="FanOutWitnessReport"/>.
/// </summary>
public sealed class WithoutFanOut : IContestant
{
    public string Family => "champion";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?> { ["fanOut"] = false };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var section = context.Record.TodayNoFanOut ?? throw new InvalidOperationException("the recording has no fan-out-off recall");
        return Decision.Of(context.Recorded("noFanOut", section), Doors.Shipped);
    }
}

/// <summary>What the adaptive router did on a split: how often its gate fired, by which rule, how many legs, how many voids.</summary>
public static class FanOutWitnessReport
{
    public static IReadOnlyDictionary<string, object> Of(IEnumerable<MatrixItem> items, ArenaData data)
    {
        var witnesses = items.Select(i => (Item: i, Witness: data.Records[i.Id].FanOut)).ToList();
        var fired = witnesses.Where(w => w.Witness?.GateFired == true).ToList();
        return new Dictionary<string, object>
        {
            ["turns"] = witnesses.Count,
            ["plannerRan"] = witnesses.Count(w => w.Witness is not null),
            ["gateFired"] = fired.Count,
            ["byRule"] = fired.SelectMany(w => w.Witness!.Rules).GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count()),
            ["withLegs"] = fired.Count(w => w.Witness!.Legs.Count > 0),
            ["voided"] = fired.Count(w => w.Witness!.VoidReason is not null),
            ["voidReasons"] = fired.Where(w => w.Witness!.VoidReason is not null).GroupBy(w => w.Witness!.VoidReason!).ToDictionary(g => g.Key, g => g.Count()),
            ["legsAddingSomething"] = fired.Sum(w => w.Witness!.Legs.Count(l => l.Survived > 0)),
            ["firedOn"] = fired.GroupBy(w => w.Item.Category).ToDictionary(g => g.Key, g => g.Count()),
        };
    }
}
