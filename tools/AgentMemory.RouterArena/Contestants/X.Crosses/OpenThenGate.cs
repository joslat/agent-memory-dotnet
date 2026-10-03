using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// A cross: one contestant chooses the doors before the search (rules, examples, a model, JEV), and only the doors it
/// opened are searched wide open, each through the gatekeeper's own gate (its floor, margin and top) after the search. The
/// reading doors it opened let in what they read. The owner's two gates in one router: at the door, and at the threshold.
/// </summary>
public sealed class OpenThenGate(IContestant opener, string openerName, GateSettings gates) : IContestant
{
    public string Family => "cross";

    public IReadOnlyDictionary<string, object?> Parameters
    {
        get
        {
            var p = new Dictionary<string, object?>(gates.ToParameters()) { ["opener"] = openerName };
            p.Remove("reading");
            return p;
        }
    }

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var first = opener.Decide(item, context);
        var open = first.Opened;
        var candidates = context.Source(gates.Rewrite, wide: true);
        if (open.Contains(Door.Temporal) && context.Record.Doors?.GetValueOrDefault("temporal:wide") is { } current)
            candidates = [.. candidates.Where(c => c.Section != RecallSection.Facts),
                .. Candidates.Of(context.Data.World, current).Where(c => c.Section == RecallSection.Facts)];
        var kept = new List<Candidate>();
        foreach (var door in Doors.Searching)
        {
            // The temporal door reads the facts: with it open, the facts' gates run on the currently valid ones.
            if (!open.Contains(door) && !(door == Door.Semantic && open.Contains(Door.Temporal))) continue;
            var mine = candidates.Where(c => c.Door == door && c.Score is not null).OrderByDescending(c => c.Score!.Value).ToList();
            if (mine.Count == 0) continue;
            var best = mine[0].Score!.Value;
            var floor = gates.Floor.GetValueOrDefault(door, gates.DefaultFloor);
            var chosen = mine.Where(c => c.Score >= floor && (gates.Margin is null || c.Score >= best - gates.Margin)).ToList();
            var top = gates.Top.TryGetValue(door, out var t) ? t : gates.DefaultTop;
            kept.AddRange(top is > 0 ? chosen.Take(top.Value) : chosen);
        }
        foreach (var door in Doors.Reading.Where(d => d != Door.Temporal && open.Contains(d))) kept.AddRange(context.Reading(door));
        return new Decision(kept.Select(c => c.Id).ToHashSet(StringComparer.Ordinal), open, Doors.SearchesOf(open.ToHashSet()),
            first.ModelCalls, first.Lane) { ModelSeconds = first.ModelSeconds, Wide = true, SearchedDoors = open };
    }
}
