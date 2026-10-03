using AgentMemory.RouterArena.Contestants;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Arena;

/// <summary>
/// The rounds of the fight. Round 1: every contestant on the tuning turns, untuned. Round 2: tuned on the tuning turns
/// only, then crossed. The final: the frozen finalists on the held-out turns, read once. Every round is logged.
/// </summary>
public sealed class Rounds(ArenaData data, string home, TextWriter output)
{
    private readonly Dictionary<string, TurnContext> _contexts = new(StringComparer.Ordinal);

    private IReadOnlyList<MatrixItem> Split(string split) => [.. data.Matrix.InSplit(split)];

    public ContestantScore Score(string split, IContestant contestant) => Referee.Score(Split(split), data, contestant, _contexts);

    public Dictionary<string, IContestant> Round1Roster()
    {
        var roster = new Dictionary<string, IContestant>
        {
        ["C00 oracle"] = new Oracle(),
        ["C01 search everything (today)"] = new OldMethod(),
        ["C01w search everything, wide"] = new WideOpen(),
        ["C02 rules v1"] = new RulesV1(),
        ["C03 rules v2"] = new RulesV2(),
        ["C04 old method without fan-out"] = new WithoutFanOut(),
        ["C05 gates: floor 0.75"] = new Gatekeeper(new GateSettings { DefaultFloor = 0.75 }),
        ["C05 gates: margin 0.05"] = new Gatekeeper(new GateSettings { Margin = 0.05 }),
        ["C05 gates: floor 0.72, margin 0.06, top 5"] = new Gatekeeper(new GateSettings { DefaultFloor = 0.72, Margin = 0.06, DefaultTop = 5 }),
        ["C07 nearest examples (k 7, 30%)"] = new NearestExamples(),
        ["C08 model lane"] = new ModelLane(),
        ["C09 two lanes: rules v2 | everything"] = new TwoLanes(new RulesV2(), new OldMethod(), "C03", "C01"),
        ["C09 two lanes: examples | model"] = new TwoLanes(new NearestExamples(), new ModelLane(), "C07", "C08"),
        ["C10 rewrite + everything"] = new OldMethod(rewrite: true),
        ["C11 one pool, best 12"] = new OnePool(12),
        ["C13 gates + abstain 0.74"] = new Gatekeeper(new GateSettings { DefaultFloor = 0.72, Margin = 0.06, DefaultTop = 5, Abstain = 0.74 }),
        ["C06 door gates (k 9, 30%)"] = new DoorGates(new Dictionary<Door, double>()),
        };
        // JEV's contestants fight when its answers were recorded.
        if (data.Jev.ContainsKey("doors")) roster["C18 JEV one for all (0.5)"] = new JevDoors();
        if (data.Jev.ContainsKey("gates"))
        {
            roster["C19 JEV gate per door (0.5)"] = new JevGates();
            roster["C19s JEV gate per door, admits what it saw (0.5)"] = new JevGates(shown: true);
        }
        if (data.Jev.ContainsKey("items")) roster["C20 JEV gate per memory (0.5)"] = new JevItems();
        return roster;
    }

    public string Run(string round, string split, IReadOnlyDictionary<string, IContestant> roster, string notes)
    {
        var results = roster.ToDictionary(c => c.Key, c => Score(split, c.Value));
        var extra = new Dictionary<string, object> { ["fanOutWitness"] = FanOutWitnessReport.Of(Split(split), data) };
        if ((split == "heldout" || round.StartsWith("final", StringComparison.Ordinal)) && results.ContainsKey("C01 search everything (today)"))
        {
            extra["verdict"] = Final.Verdict(results);
            output.WriteLine(extra["verdict"]);
        }
        var folder = RunLog.Write(home, round, split, data, results, notes, extra);
        output.WriteLine($"{round} on {split}: {Split(split).Count} turns, run log {folder}");
        Print(results);
        return folder;
    }

    public void Print(IReadOnlyDictionary<string, ContestantScore> results)
    {
        output.WriteLine($"{"contestant",-46} {"served",10} {"route",6} {"search",7} {"door R/P",11} {"exact",6} {"doors",6} {"noise",6} {"size",6} {"srch",5} {"model",6} {"quiet",6}");
        foreach (var (id, r) in results.OrderByDescending(r => r.Value.Served).ThenBy(r => r.Value.NoisePerTurn))
            output.WriteLine($"{id,-46} {r.Served,4}/{r.Needing,-5} {r.RoutingMisses,6} {r.RetrievalMisses,7} {r.DoorRecall,5:0.###}/{r.DoorPrecision,-5:0.###} {r.DoorsExact,6} {r.DoorsPerTurn,6} {r.NoisePerTurn,6} {r.SizePerTurn,6} {r.SearchesPerTurn,5} {r.ModelCallsPerTurn,6} {r.Quiet,2}/{r.NothingNeeded}");
    }

    // ---- Round 2: tuning on the tuning turns, under the champion's noise --------------------------------------------

    /// <summary>Served first, then noise; a configuration over the noise budget loses to every one under it.</summary>
    private static (bool, int, double) Objective(ContestantScore r, double budget) => (r.NoisePerTurn <= budget, r.Served, -r.NoisePerTurn);

    private static bool Better((bool Within, int Served, double Noise) a, (bool Within, int Served, double Noise) b) =>
        a.Within != b.Within ? a.Within : a.Served != b.Served ? a.Served > b.Served : a.Noise > b.Noise;

    /// <summary>Coordinate search: the best of a coarse grid, then one parameter at a time until nothing improves.</summary>
    private (T Params, ContestantScore Score) Tune<T>(IEnumerable<T> coarse, Func<T, IContestant> make,
        IReadOnlyList<(string Name, IReadOnlyList<Func<T, T>> Moves)> fine, double budget) =>
        Tune(coarse, make, fine, (s, b) => Better(Objective(s, budget), Objective(b, budget)));

    /// <summary>Coordinate search under any objective: <paramref name="better"/> says whether a score beats the best so far.</summary>
    private (T Params, ContestantScore Score) Tune<T>(IEnumerable<T> coarse, Func<T, IContestant> make,
        IReadOnlyList<(string Name, IReadOnlyList<Func<T, T>> Moves)> fine, Func<ContestantScore, ContestantScore, bool> better)
    {
        T best = default!;
        ContestantScore? bestScore = null;
        foreach (var p in coarse)
        {
            var s = Score("dev", make(p));
            if (bestScore is null || better(s, bestScore)) (best, bestScore) = (p, s);
        }
        var improved = true;
        while (improved)
        {
            improved = false;
            foreach (var (_, moves) in fine)
                foreach (var move in moves)
                {
                    var p = move(best);
                    var s = Score("dev", make(p));
                    if (better(s, bestScore!)) (best, bestScore, improved) = (p, s, true);
                }
        }
        return (best, bestScore!);
    }

    private static readonly double[] FloorMoves = [0.6, 0.62, 0.64, 0.66, 0.68, 0.7, 0.72];
    private static readonly double?[] MarginMoves = [null, 0.04, 0.06, 0.08, 0.1, 0.12, 0.15];
    private static readonly int?[] TopMoves = [2, 3, 4, 5, 6, 8, null];
    private static readonly double[] DoorFloorMoves = [0.55, 0.6, 0.62, 0.64, 0.66, 0.68, 0.7, 0.72, 0.75, 0.8];
    private static readonly int?[] DoorTopMoves = [1, 2, 3, 4, 5, 6, 8, 10, null];

    /// <summary>The gatekeeper's moves: floor, margin, top, then each searching door's floor and top.</summary>
    private static List<(string, IReadOnlyList<Func<GateSettings, GateSettings>>)> GateMoves()
    {
        var moves = new List<(string, IReadOnlyList<Func<GateSettings, GateSettings>>)>
        {
            ("floor", [.. FloorMoves.Select(v => (Func<GateSettings, GateSettings>)(g => g with { DefaultFloor = v }))]),
            ("margin", [.. MarginMoves.Select(v => (Func<GateSettings, GateSettings>)(g => g with { Margin = v }))]),
            ("top", [.. TopMoves.Select(v => (Func<GateSettings, GateSettings>)(g => g with { DefaultTop = v }))]),
        };
        foreach (var door in Doors.Searching)
        {
            moves.Add(($"floor_{Doors.Name(door)}", [.. DoorFloorMoves.Select(v => (Func<GateSettings, GateSettings>)(g => g.WithFloor(door, v)))]));
            moves.Add(($"top_{Doors.Name(door)}", [.. DoorTopMoves.Select(v => (Func<GateSettings, GateSettings>)(g => g.WithTop(door, v)))]));
        }
        return moves;
    }

    /// <summary>
    /// Round 4 (40.75): the gate per memory improved on the tuning turns only — each door's own threshold, a cap per door by
    /// P(yes), quiet on small talk, JEV before the search judging the time doors — for every recorded way of asking (six
    /// memories shown, more shown, more context, a local model). Two objectives: the most served under today's noise; and
    /// the least noise that serves at least as many as round 3's winner.
    /// </summary>
    public Dictionary<string, IContestant> Round4Roster(IReadOnlySet<string>? only = null)
    {
        var budget = Score("dev", new OldMethod()).NoisePerTurn;
        var winner = Score("dev", new JevItems(0.2));
        var roster = new Dictionary<string, IContestant> { ["C00 oracle"] = new Oracle(), ["C01 search everything (today)"] = new OldMethod(), ["C20t JEV gate per memory, tuned"] = new JevItems(0.2) };
        IContestant? judge = null;
        if (data.Jev.ContainsKey("doors"))
        {
            var doorThresholds = new[] { 0.15, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.01 };
            var jevMoves = Doors.All.Select(d => ($"jev_{Doors.Name(d)}", (IReadOnlyList<Func<DoorSettings, DoorSettings>>)
                [.. doorThresholds.Select(v => (Func<DoorSettings, DoorSettings>)(s => s.With(d, v)))])).ToList();
            var (one, _) = Tune(new[] { 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8 }.Select(v => new DoorSettings(v, new Dictionary<Door, double>())),
                s => new JevDoors(s.Default, s.Doors), jevMoves, budget);
            judge = new JevDoors(one.Default, one.Doors);
        }
        double[] perDoor = [0.1, 0.15, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.6, 0.7, 0.8, 1.01];
        var moves = new List<(string, IReadOnlyList<Func<JevItemSettings, JevItemSettings>>)>
        {
            ("threshold", [.. new[] { 0.1, 0.15, 0.2, 0.25, 0.3, 0.4 }.Select(v => (Func<JevItemSettings, JevItemSettings>)(s => s with { Threshold = v }))]),
            ("top", [.. new int?[] { null, 1, 2, 3, 4, 6, 8 }.Select(v => (Func<JevItemSettings, JevItemSettings>)(s => s with { Top = v }))]),
            ("quiet", [s => s with { Quiet = true }, s => s with { Quiet = false }]),
            ("uncapLists", [s => s with { UncapLists = true }, s => s with { UncapLists = false }]),
        };
        // The cascade, tuned on its own from the best settings: how much the search's score can decide without the model.
        var cascade = new List<(string, IReadOnlyList<Func<JevItemSettings, JevItemSettings>>)>
        {
            ("autoAdmit", [.. new double?[] { null, 0.78, 0.8, 0.82, 0.85, 0.9 }.Select(v => (Func<JevItemSettings, JevItemSettings>)(s => s with { AutoAdmit = v }))]),
            ("autoDrop", [.. new double?[] { null, 0.4, 0.45, 0.5, 0.55, 0.6 }.Select(v => (Func<JevItemSettings, JevItemSettings>)(s => s with { AutoDrop = v }))]),
        };
        foreach (var door in Doors.All)
            moves.Add(($"threshold_{Doors.Name(door)}", [.. perDoor.Select(v => (Func<JevItemSettings, JevItemSettings>)(s => s.WithDoor(door, v)))]));
        bool Lean(ContestantScore s, ContestantScore best) =>
            (s.Served >= winner.Served) != (best.Served >= winner.Served) ? s.Served >= winner.Served
            : s.Served < winner.Served ? s.Served > best.Served
            : s.NoisePerTurn != best.NoisePerTurn ? s.NoisePerTurn < best.NoisePerTurn : s.Served > best.Served;
        foreach (var answers in data.Jev.Keys.Where(k => k.StartsWith("items", StringComparison.Ordinal) && (only is null || only.Contains(k))).Order(StringComparer.Ordinal))
        {
            var start = new[] { 0.15, 0.2, 0.3 }.Select(v => new JevItemSettings { Threshold = v, Answers = answers });
            var (most, mostScore) = Tune(start, s => new JevItems(s), moves, budget);
            var (lean, leanScore) = Tune([most], s => new JevItems(s), moves, Lean);
            roster[$"C20m {answers}: most served"] = new JevItems(most);
            roster[$"C20l {answers}: lean"] = new JevItems(lean);
            // The cascade must not serve fewer: the fewest model calls at the most-served setting's served and noise.
            bool Cheaper(ContestantScore s, ContestantScore best) =>
                (s.Served >= mostScore.Served && s.NoisePerTurn <= mostScore.NoisePerTurn + 0.25) != (best.Served >= mostScore.Served && best.NoisePerTurn <= mostScore.NoisePerTurn + 0.25)
                    ? s.Served >= mostScore.Served && s.NoisePerTurn <= mostScore.NoisePerTurn + 0.25
                    : s.ModelCallsPerTurn != best.ModelCallsPerTurn ? s.ModelCallsPerTurn < best.ModelCallsPerTurn : s.Served > best.Served;
            var (cheap, cheapScore) = Tune([most], s => new JevItems(s), cascade, Cheaper);
            roster[$"C21 {answers}: cascade (the search decides the clear cases)"] = new JevItems(cheap);
            output.WriteLine($"{answers} cascade: {cheapScore.Served} at {cheapScore.NoisePerTurn}, {cheapScore.ModelCallsPerTurn} calls a turn");
            output.WriteLine($"{answers}: most served {mostScore.Served} at {mostScore.NoisePerTurn}; lean {leanScore.Served} at {leanScore.NoisePerTurn}");
            if (judge is not null)
            {
                var timed = new JevItemSettings { Threshold = most.Threshold, Answers = answers, TimeJudge = judge, TimeJudgeName = "C18t" };
                var (cross, crossScore) = Tune([timed with { PerDoor = most.PerDoor, Top = most.Top, Quiet = most.Quiet }, timed], s => new JevItems(s), moves, budget);
                roster[$"X8 {answers}: JEV before the search on the time doors"] = new JevItems(cross);
                output.WriteLine($"{answers} + time judge: {crossScore.Served} at {crossScore.NoisePerTurn}");
            }
        }
        return roster;
    }

    /// <summary>40.73: who is timed: today's recall and its variants, the routers without a model, and every tuned finalist and JEV variant.</summary>
    public Dictionary<string, IContestant> TimeRoster()
    {
        var roster = new Dictionary<string, IContestant>
        {
            ["C01 search everything (today)"] = new OldMethod(), ["C04 old method without fan-out"] = new WithoutFanOut(),
            ["C02 rules v1"] = new RulesV1(), ["C03 rules v2"] = new RulesV2(), ["C08 model lane"] = new ModelLane(),
            ["C01w search everything, wide"] = new WideOpen(),
        };
        var tuned = Round2Roster();
        foreach (var name in Final.Finalists.Append("X7 JEV: one for all on the time doors, gates on the rest").Where(tuned.ContainsKey)) roster[name] = tuned[name];
        // 40.80: the local gate and the hybrid (their answers recorded by train_gate.py, the local model's time with them).
        if (data.Jev.ContainsKey("items-xenc-mini-acc"))
        {
            roster["C24 local gate alone (cross-encoder, 0.279)"] = new JevItems(new JevItemSettings { Threshold = 0.2787, Answers = "items-xenc-mini-acc" });
            if (data.Jev.ContainsKey("items-k12ctx"))
            {
                roster["C22 JEV gate per memory, plain (12 shown, context, examples, 0.25)"] = new JevItems(new JevItemSettings { Threshold = 0.25, Answers = "items-k12ctx" });
                roster["C23 hybrid: local gate first, JEV on the unsure types"] = new GateCascade("items-xenc-mini-acc", "items-k12ctx", 0.2, 0.8, 0.25);
            }
        }
        // The improved gates differ only in what the model was asked; their search is the same, so one answer set stands for them.
        foreach (var (name, contestant) in Round4Roster(new HashSet<string> { "items-k12ctx" }).Where(r => r.Key.StartsWith("C20", StringComparison.Ordinal) || r.Key.StartsWith("X8", StringComparison.Ordinal)))
            roster.TryAdd(name, contestant);
        return roster;
    }

    /// <summary>The final's roster: the bounds, today's recall, and the finalists as round 2 tuned them (the tuning is deterministic).</summary>
    public Dictionary<string, IContestant> FinalRoster()
    {
        var tuned = Round2Roster();
        var roster = new Dictionary<string, IContestant>
        {
            ["C00 oracle"] = new Oracle(), ["C01w search everything, wide"] = new WideOpen(), ["C01 search everything (today)"] = new OldMethod(),
        };
        foreach (var name in Final.Finalists) roster[name] = tuned[name];
        return roster;
    }

    public Dictionary<string, IContestant> Round2Roster()
    {
        var champion = Score("dev", new OldMethod());
        var budget = champion.NoisePerTurn;
        var found = new Dictionary<string, IContestant>();

        // The old method, re-tuned: its best recorded setting (real recalls, not a simulation).
        var settings = data.Records.Values.First().Variants?.Keys.Order(StringComparer.Ordinal).ToList() ?? [];
        var old = settings.ToDictionary(s => s, s => Score("dev", new OldMethod(s)));
        foreach (var (s, r) in old) output.WriteLine($"old method {s}: served {r.Served}, noise {r.NoisePerTurn}");
        var within = settings.Where(s => old[s].NoisePerTurn <= budget).ToList();
        if (within.Count > 0)
            found["C01t old method, re-tuned"] = new OldMethod(within.OrderByDescending(s => old[s].Served).ThenBy(s => old[s].NoisePerTurn).First());
        if (settings.Count > 0)
            found["C01m old method, most served"] = new OldMethod(settings.OrderByDescending(s => old[s].Served).ThenBy(s => old[s].NoisePerTurn).First());

        // The gatekeeper, tuned per door under the champion's noise; then the least noise that still serves as many.
        var coarse = from f in new[] { 0.62, 0.66, 0.7 }
                     from m in new double?[] { null, 0.06, 0.1 }
                     from t in new int?[] { 3, 5, 8 }
                     select new GateSettings { DefaultFloor = f, Margin = m, DefaultTop = t };
        var (balanced, balancedScore) = Tune(coarse, g => new Gatekeeper(g), GateMoves(), budget);
        found["C05t gatekeeper (champion noise)"] = new Gatekeeper(balanced);
        output.WriteLine($"tuned gatekeeper: served {balancedScore.Served}, noise {balancedScore.NoisePerTurn}");
        var lean = balanced;
        foreach (var share in new[] { 0.8, 0.6, 0.5, 0.4, 0.3 })
        {
            var (q, s) = Tune([lean], g => new Gatekeeper(g), GateMoves(), budget * share);
            if (s.Served >= champion.Served && s.NoisePerTurn <= budget * share) lean = q;
        }
        found["C05l gatekeeper, lean"] = new Gatekeeper(lean);

        // The door gates, tuned per door.
        var doorCoarse = new[] { 0.1, 0.15, 0.2, 0.25, 0.3, 0.4 }.Select(s => new DoorSettings(s, new Dictionary<Door, double>()));
        var doorMoves = Doors.All.Select(d => ($"door_{Doors.Name(d)}", (IReadOnlyList<Func<DoorSettings, DoorSettings>>)
            [.. new[] { 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5 }.Select(v => (Func<DoorSettings, DoorSettings>)(s => s.With(d, v)))])).ToList();
        var (gates, _) = Tune(doorCoarse, d => new DoorGates(d.Doors, d.Default), doorMoves, budget);
        found["C06t door gates (before search)"] = new DoorGates(gates.Doors, gates.Default);

        // JEV, tuned: one threshold, then (one for all) each door's own.
        double[] thresholds = [0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8];
        if (data.Jev.ContainsKey("doors"))
        {
            var doorThresholds = new[] { 0.15, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.01 };
            var jevMoves = Doors.All.Select(d => ($"jev_{Doors.Name(d)}", (IReadOnlyList<Func<DoorSettings, DoorSettings>>)
                [.. doorThresholds.Select(v => (Func<DoorSettings, DoorSettings>)(s => s.With(d, v)))])).ToList();
            var (jev, jevScore) = Tune(thresholds.Select(v => new DoorSettings(v, new Dictionary<Door, double>())), s => new JevDoors(s.Default, s.Doors), jevMoves, budget);
            found["C18t JEV one for all, tuned"] = new JevDoors(jev.Default, jev.Doors);
            output.WriteLine($"tuned JEV one for all: served {jevScore.Served}, noise {jevScore.NoisePerTurn}, doors {jevScore.DoorRecall}/{jevScore.DoorPrecision}");
            found["X4 JEV opens, gates cut"] = new OpenThenGate(new JevDoors(jev.Default, jev.Doors), "C18t", balanced);
            found["X5 two lanes: rules v2 | JEV"] = new TwoLanes(new RulesV2(), new JevDoors(jev.Default, jev.Doors), "C03", "C18t");
            // JEV tells the specialist doors apart, not the broad ones: the broad doors always open, cut by their gates.
            Door[] broad = [Door.Semantic, Door.Episodic, Door.Working];
            found["X6 JEV for the special doors, gates for the broad"] =
                new OpenThenGate(new AlwaysOpen(new JevDoors(jev.Default, jev.Doors), "C18t", broad), "C18t + broad always", balanced);
        }
        if (data.Jev.ContainsKey("gates"))
        {
            var (gate, _) = Tune(thresholds.SelectMany(v => new[] { (v, false), (v, true) }), g => new JevGates(g.Item1, g.Item2), [], budget);
            found["C19t JEV gate per door, tuned"] = new JevGates(gate.Item1, gate.Item2);
            if (found.TryGetValue("C18t JEV one for all, tuned", out var oneForAll))
            {
                // JEV before the search judges the time in the question; its gates after the search judge the memories.
                var time = new HashSet<Door> { Door.Temporal, Door.BiTemporal, Door.Working };
                found["X7 JEV: one for all on the time doors, gates on the rest"] =
                    new DoorMix(oneForAll, "C18t", time, new JevGates(gate.Item1, gate.Item2), "C19t");
            }
        }
        if (data.Jev.ContainsKey("items"))
        {
            var (item, _) = Tune(thresholds.Concat([0.35, 0.45, 0.55, 0.65]), v => new JevItems(v), [], budget);
            found["C20t JEV gate per memory, tuned"] = new JevItems(item);
        }

        // Crosses.
        found["X1 gatekeeper + rewrite"] = new Gatekeeper(balanced with { Rewrite = true });
        found["X2 gatekeeper lean + rewrite"] = new Gatekeeper(lean with { Rewrite = true });
        found["X3 two lanes: model | gatekeeper"] = new TwoLanes(new ModelLane(), new Gatekeeper(balanced), "C08", "C05t");

        return new Dictionary<string, IContestant>
        {
            ["C00 oracle"] = new Oracle(),
            ["C01 search everything (today)"] = new OldMethod(),
            ["C01w search everything, wide"] = new WideOpen(),
            ["C04 old method without fan-out"] = new WithoutFanOut(),
        }.Concat(found).ToDictionary(c => c.Key, c => c.Value);
    }
}

/// <summary>
/// The final (40.70, 40.72): the finalists frozen as round 2 tuned them on the tuning turns, fought once on the held-out
/// turns. The rule, written before the held-out turns were read: a finalist replaces today's recall only if, on the
/// held-out turns, it serves at least as many turns, with no more noise per turn and no lower door recall; among those,
/// the most served wins, then the least noise, then the fewest model calls, then the fewest searches (the simpler). The
/// best finalist without a model is named too: a model on the recall path is the owner's decision.
/// </summary>
public static class Final
{
    /// <summary>
    /// 40.77, written before the fresh turns were labelled, recorded or asked about: the finalists after round 4, every one
    /// tuned on the v2 tuning turns only, fought once on 120 turns nobody tuned on. The same rule as the first final.
    /// </summary>
    public static readonly string[] FreshFinalists =
    [
        "C20t JEV gate per memory, tuned", "C20m items-k12ctx: most served", "C20l items-k12ctx: lean",
        "X6 JEV for the special doors, gates for the broad", "C06t door gates (before search)", "C05t gatekeeper (champion noise)",
    ];

    public const string FreshRule =
        "Fresh final (40.77): the finalists after round 4, tuned on the v2 tuning turns only, read once on 120 fresh turns written and "
        + "labelled blind after the first final. " + "A finalist replaces today's recall only if it serves at least as many turns, with no more "
        + "noise per turn and no lower door recall; among those the most served wins, then the least noise, then the fewest model calls, "
        + "then the fewest searches. The best finalist without a model is named too.";

    public static readonly string[] Finalists =
    [
        "C20t JEV gate per memory, tuned", "C19t JEV gate per door, tuned", "X6 JEV for the special doors, gates for the broad",
        "C18t JEV one for all, tuned", "C05t gatekeeper (champion noise)", "C06t door gates (before search)",
    ];

    public const string Rule =
        "Final: the finalists frozen as round 2 tuned them on the tuning turns, read once on the held-out turns. A finalist replaces "
        + "today's recall only if it serves at least as many held-out turns, with no more noise per turn and no lower door recall; "
        + "among those the most served wins, then the least noise, then the fewest model calls, then the fewest searches. The best "
        + "finalist without a model is named too.";

    public static string Verdict(IReadOnlyDictionary<string, ContestantScore> results)
    {
        var today = results["C01 search everything (today)"];
        var beating = Finalists.Concat(FreshFinalists).Distinct().Where(results.ContainsKey).Select(f => (Name: f, Score: results[f]))
            .Where(f => f.Score.Served >= today.Served && f.Score.NoisePerTurn <= today.NoisePerTurn && f.Score.DoorRecall >= today.DoorRecall)
            .OrderByDescending(f => f.Score.Served).ThenBy(f => f.Score.NoisePerTurn).ThenBy(f => f.Score.ModelCallsPerTurn).ThenBy(f => f.Score.SearchesPerTurn)
            .ToList();
        var noModel = beating.FirstOrDefault(f => f.Score.ModelCallsPerTurn == 0);
        return beating.Count == 0
            ? $"No finalist beats today's recall (served {today.Served}, noise {today.NoisePerTurn}, door recall {today.DoorRecall})."
            : $"Winner: {beating[0].Name} (served {beating[0].Score.Served}/{beating[0].Score.Needing}, noise {beating[0].Score.NoisePerTurn}, "
              + $"door recall {beating[0].Score.DoorRecall}) against today's {today.Served}, {today.NoisePerTurn}, {today.DoorRecall}. "
              + $"Beating today: {string.Join("; ", beating.Select(f => f.Name))}. "
              + (noModel.Name is null ? "No finalist without a model beats today." : $"Best without a model: {noModel.Name}.");
    }
}

/// <summary>Door-gate thresholds: one default and a share per door.</summary>
public sealed record DoorSettings(double Default, IReadOnlyDictionary<Door, double> Doors)
{
    public DoorSettings With(Door door, double value) => this with { Doors = new Dictionary<Door, double>(Doors) { [door] = value } };
}
