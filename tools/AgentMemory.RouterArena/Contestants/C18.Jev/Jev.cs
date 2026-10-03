using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Ask;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 18 · JEV, one for all, before the search (the owner's idea): the decision model reads the turn and the conversation and
/// answers twelve yes/no questions in one call, one per door ("does the reply need this type of memory?"); a door opens
/// when P(yes) reaches its threshold. The answers were recorded once (<c>ask-jev --mode doors</c>) and are replayed, free.
/// A turn whose call failed opens what the old method reads.
/// </summary>
public sealed class JevDoors(double threshold = 0.5, IReadOnlyDictionary<Door, double>? perDoor = null) : IContestant
{
    public IReadOnlyDictionary<Door, double> PerDoor { get; } = perDoor ?? new Dictionary<Door, double>();

    public double Threshold { get; } = threshold;

    public string Family => "jev";

    public IReadOnlyDictionary<string, object?> Parameters => Jev.Parameters("doors (one call, twelve questions)", Threshold, PerDoor);

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        if (Jev.Answer(context, "doors", item) is not { } answer)
            return Decision.Of(context.Source(false, wide: false), Doors.Shipped, model: 1, lane: "jev-failed");
        var open = Doors.All.Where(d => answer.Yes.GetValueOrDefault(Doors.Name(d)) >= PerDoor.GetValueOrDefault(d, Threshold));
        return context.Open(open, model: answer.Calls, lane: "jev") with { ModelSeconds = answer.Seconds };
    }
}

/// <summary>
/// 19 · JEV, a gate at every door, after the search (the owner's idea: "a gate for each memory type with JEV"): every door
/// is searched; each door's gate is shown its best memories for the turn and asked whether they belong in front of the
/// assistant; a door whose P(yes) reaches the threshold is opened and lets in what the shipped recall keeps of it
/// (<c>shown: false</c>) or every memory its gate was shown (<c>shown: true</c>). One call per door with memories, all at
/// once: the turn waits for the slowest.
/// </summary>
public sealed class JevGates(double threshold = 0.5, bool shown = false, IReadOnlyDictionary<Door, double>? perDoor = null) : IContestant
{
    public IReadOnlyDictionary<Door, double> PerDoor { get; } = perDoor ?? new Dictionary<Door, double>();

    public double Threshold { get; } = threshold;

    public string Family => "jev";

    public IReadOnlyDictionary<string, object?> Parameters
    {
        get
        {
            var p = Jev.Parameters("gates (one call per door, after the search)", Threshold, PerDoor);
            p["admit"] = shown ? "what the gate was shown" : "what the shipped recall keeps";
            return p;
        }
    }

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        if (Jev.Answer(context, "gates", item) is not { } answer)
            return Decision.Of(context.Source(false, wide: false), Doors.Shipped, model: 1, lane: "jev-failed");
        var open = Doors.All.Where(d => answer.Yes.TryGetValue(Doors.Name(d), out var p) && p >= PerDoor.GetValueOrDefault(d, Threshold)).ToHashSet();
        var admitted = shown
            ? open.SelectMany(d => JevAsk.Shown(d, context)).Select(s => s.Id).Where(Jev.IsMemory).ToHashSet(StringComparer.Ordinal)
            : context.Open(open).Admitted;
        return new Decision(admitted, open, Jev.EverySearch, answer.Calls, "jev") { ModelSeconds = answer.Seconds, Wide = true, SearchedDoors = Jev.AllDoors };
    }
}

/// <summary>
/// 20 · JEV, a gate for every memory, after the search: every door is searched; one call per door asks one yes/no
/// question per memory its gate was shown ("should this memory be in front of the assistant?"); a memory goes in when its
/// P(yes) reaches its door's threshold, at most <see cref="JevItemSettings.Top"/> per door by P(yes), and a door is open
/// when something of it went in. 40.75: <see cref="JevItemSettings.Quiet"/> lets small talk read nothing;
/// <see cref="JevItemSettings.TimeJudge"/> lets another contestant (JEV before the search, which hears time in the
/// question) decide the time doors, the per-memory gates the rest; <see cref="JevItemSettings.Answers"/> replays another
/// recorded way of asking (more memories shown, more context, a local model).
/// </summary>
public sealed class JevItems(JevItemSettings settings) : IContestant
{
    public JevItems(double threshold = 0.5) : this(new JevItemSettings { Threshold = threshold })
    {
    }

    public JevItemSettings Settings { get; } = settings;

    public double Threshold => Settings.Threshold;

    public string Family => "jev";

    public IReadOnlyDictionary<string, object?> Parameters
    {
        get
        {
            var p = Jev.Parameters($"{Settings.Answers} (one call per door, one question per memory)", Settings.Threshold, Settings.PerDoor);
            if (Settings.Top is { } top) p["top"] = top;
            if (Settings.Quiet) p["quiet"] = "small talk reads nothing";
            if (Settings.TimeJudgeName is { } judge) p["timeDoors"] = judge;
            if (Settings.UncapLists) p["uncapLists"] = true;
            if (Settings.AutoAdmit is { } admit) p["autoAdmit"] = admit;
            if (Settings.AutoDrop is { } drop) p["autoDrop"] = drop;
            return p;
        }
    }

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        if (Settings.Quiet && RulesV2.IsSmallTalk(item.Text)) return Decision.Nothing(lane: "quiet");
        if (Jev.Answer(context, Settings.Answers, item) is not { } answer)
            return Decision.Of(context.Source(false, wide: false), Doors.Shipped, model: 1, lane: "jev-failed");
        var judge = Settings.TimeJudge?.Decide(item, context);
        // A list, a count or "all of them": no cap per door (rules v2's cue words).
        var uncapped = Settings.UncapLists && RulesV2.Cued(item).Contains(Door.Derived);
        // The cascade: the search's own score decides the clear cases; the model is asked only about the doubtful band.
        var scores = Settings.AutoAdmit is null && Settings.AutoDrop is null
            ? null
            : context.Source(false, wide: true).Where(c => c.Score is not null)
                .GroupBy(c => $"{Doors.Name(c.Door)}|{c.Id}").ToDictionary(g => g.Key, g => g.Max(c => c.Score!.Value), StringComparer.Ordinal);
        var kept = new List<(Door Door, string Id)>();
        var asked = new HashSet<Door>();
        foreach (var group in answer.Yes.Select(a => (Key: a.Key, Parts: a.Key.Split('|', 2), P: a.Value))
                     .Select(a => (a.Key, Door: Doors.Parse(a.Parts[0]), Id: a.Parts[1], a.P))
                     .GroupBy(a => a.Door))
        {
            var door = group.Key;
            if (judge is not null && JevItemSettings.TimeDoors.Contains(door) && !judge.Opened.Contains(door)) continue;
            var passed = new List<(string Id, double P)>();
            foreach (var a in group)
            {
                double? score = scores is not null && scores.TryGetValue(a.Key, out var s) ? s : null;
                if (score >= Settings.AutoAdmit) passed.Add((a.Id, 1 + score.Value));
                else if (score < Settings.AutoDrop) continue;
                else
                {
                    asked.Add(door);
                    if (a.P >= Settings.PerDoor.GetValueOrDefault(door, Settings.Threshold)) passed.Add((a.Id, a.P));
                }
            }
            var ranked = passed.OrderByDescending(a => a.P);
            kept.AddRange((Settings.Top is { } top && !uncapped ? ranked.Take(top) : ranked).Select(a => (door, a.Id)));
        }
        // A door whose memories the search decided alone needs no call.
        var calls = scores is null ? answer.Calls : asked.Count;
        return new Decision(
            kept.Select(k => k.Id).Where(Jev.IsMemory).ToHashSet(StringComparer.Ordinal),
            kept.Select(k => k.Door).ToHashSet(),
            Jev.EverySearch, calls + (judge?.ModelCalls ?? 0), calls == 0 && scores is not null ? "search alone" : "jev")
        {
            // The judge before the search and the gates after it run one after the other.
            ModelSeconds = (calls == 0 ? 0 : answer.Seconds) + (judge?.ModelSeconds ?? 0),
            Wide = true, SearchedDoors = Jev.AllDoors,
        };
    }
}

/// <summary>A gate-per-memory's settings: its threshold (and each door's own), its cap per door, quiet, a judge for the time doors, the recorded answers it replays.</summary>
public sealed record JevItemSettings
{
    public static readonly IReadOnlySet<Door> TimeDoors = new HashSet<Door> { Door.Temporal, Door.BiTemporal, Door.Prospective, Door.Forgetting };

    public double Threshold { get; init; } = 0.5;
    public IReadOnlyDictionary<Door, double> PerDoor { get; init; } = new Dictionary<Door, double>();
    public int? Top { get; init; }
    public bool Quiet { get; init; }
    public IContestant? TimeJudge { get; init; }

    /// <summary>No cap per door when the turn asks for a list, a count or all of something.</summary>
    public bool UncapLists { get; init; }

    /// <summary>The cascade: a memory the search scored at or above this goes in without asking.</summary>
    public double? AutoAdmit { get; init; }

    /// <summary>The cascade: a memory the search scored below this stays out without asking.</summary>
    public double? AutoDrop { get; init; }
    public string? TimeJudgeName { get; init; }

    /// <summary>The recorded answers replayed: "items" (six shown per door), or another way of asking (<c>jev-&lt;name&gt;.json</c>).</summary>
    public string Answers { get; init; } = "items";

    public JevItemSettings WithDoor(Door door, double value) => this with { PerDoor = new Dictionary<Door, double>(PerDoor) { [door] = value } };
}

/// <summary>
/// 23 · The hybrid gate (the owner's idea, 40.79–40.81): a fast local gate (a cross-encoder trained on the labelled turns)
/// decides every memory type where it is sure — every memory it was shown below <paramref name="low"/> or above
/// <paramref name="high"/> — and JEV online decides the types it hesitates on, at <paramref name="onlineThreshold"/>. A type
/// the local gate settles costs no call; the online calls run at once, after the local pass.
/// </summary>
public sealed class GateCascade(string localAnswers, string onlineAnswers, double low, double high, double onlineThreshold) : IContestant
{
    public string Family => "cross";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>
    {
        ["local"] = localAnswers, ["online"] = onlineAnswers, ["sureBelow"] = low, ["sureAbove"] = high, ["onlineThreshold"] = onlineThreshold,
    };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        if (Jev.Answer(context, localAnswers, item) is not { } local)
            return Decision.Of(context.Source(false, wide: false), Doors.Shipped, lane: "local-failed");
        var online = Jev.Answer(context, onlineAnswers, item);
        var kept = new List<(Door Door, string Id)>();
        var asked = 0;
        foreach (var group in local.Yes.Select(a => (Parts: a.Key.Split('|', 2), P: a.Value)).GroupBy(a => Doors.Parse(a.Parts[0])))
        {
            var door = group.Key;
            if (group.All(a => a.P < low || a.P > high))
            {
                kept.AddRange(group.Where(a => a.P > high).Select(a => (door, a.Parts[1])));
                continue;
            }
            asked++;
            if (online is null) kept.AddRange(group.Where(a => a.P >= (low + high) / 2).Select(a => (door, a.Parts[1])));
            else kept.AddRange(online.Yes.Where(a => a.Key.StartsWith(Doors.Name(door) + "|", StringComparison.Ordinal) && a.Value >= onlineThreshold)
                .Select(a => (door, a.Key.Split('|', 2)[1])));
        }
        return new Decision(kept.Select(k => k.Id).Where(Jev.IsMemory).ToHashSet(StringComparer.Ordinal), kept.Select(k => k.Door).ToHashSet(),
            Jev.EverySearch, asked, asked == 0 ? "local" : "local+jev")
        {
            // The local pass, then the online calls at once (the turn waits for the slowest).
            ModelSeconds = local.Seconds + (asked > 0 ? online?.Seconds ?? 0 : 0),
            Wide = true, SearchedDoors = Jev.AllDoors,
        };
    }
}

internal static class Jev
{
    /// <summary>A gate after the search needs every door searched first.</summary>
    public static readonly int EverySearch = Doors.SearchesOf(Doors.All.ToHashSet());

    public static readonly IReadOnlySet<Door> AllDoors = Doors.All.ToHashSet();

    public static JevAnswer? Answer(TurnContext context, string mode, MatrixItem item) =>
        context.Data.Jev.TryGetValue(mode, out var answers) ? answers.GetValueOrDefault(item.Id) : null;

    /// <summary>The working door's profile and messages are shown to its gate but are not memories the referee scores.</summary>
    public static bool IsMemory(string id) => !(id.Length > 1 && id[0] == 'W' && char.IsDigit(id[1]));

    public static Dictionary<string, object?> Parameters(string asked, double threshold, IReadOnlyDictionary<Door, double>? perDoor)
    {
        var p = new Dictionary<string, object?> { ["model"] = "jev (TypeSafe System One)", ["asked"] = asked, ["threshold"] = threshold };
        foreach (var (door, value) in perDoor ?? new Dictionary<Door, double>()) p[$"threshold_{Doors.Name(door)}"] = value;
        return p;
    }
}
