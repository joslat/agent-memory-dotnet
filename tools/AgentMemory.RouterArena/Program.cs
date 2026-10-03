// The router arena (root PLAN 40.71, 40.72): the recorder, the askers, the referee, one contestant per folder behind one
// interface, run logs and a leaderboard. Verbs: record, ask-model, ask-jev, round1, round2, leaderboard, inspect.

using System.Globalization;
using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Ask;
using AgentMemory.RouterArena.Data;
using AgentMemory.RouterArena.Record;

string? Get(string name)
{
    var i = Array.IndexOf(args, $"--{name}");
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// The arena's data (the world, the frozen matrices, the recordings, the run logs) is private and lives outside this
// repository: its root comes from --data or ROUTER_ARENA_DATA.
var strategy = Get("data") ?? Environment.GetEnvironmentVariable("ROUTER_ARENA_DATA") ?? "";
if (strategy.Length == 0 && args.FirstOrDefault() is not (null or "record"))
{
    Console.WriteLine("router-arena: set the data folder with --data <folder> or ROUTER_ARENA_DATA");
    return 1;
}
var fixtures = Path.Combine(strategy, "performance", "fixtures", "routing", "matrix");
var runs = Path.Combine(strategy, "performance", "runs", "2026-10-03_routing-matrix");
// The set fought over: v2 (the eleven doors and the forgetting switch) once it is frozen, else v1 (--set v1 keeps v1).
var set = Get("set") ?? (File.Exists(Path.Combine(fixtures, "routing-matrix-v2.json")) ? "v2" : "v1");
string Answers(string v2, string v1) => set == "v2" && File.Exists(Path.Combine(runs, v2)) ? Path.Combine(runs, v2) : Path.Combine(runs, v1);
ArenaPaths Paths() => new(
    Get("matrix") ?? Path.Combine(fixtures, set == "v2" ? "routing-matrix-v2.json" : "routing-matrix-v1.json"),
    Get("recording") ?? Path.Combine(runs, set == "v2" ? "recording-v4.json" : "recording-v3.json"),
    Get("world-index") ?? Path.Combine(fixtures, set == "v2" ? "world-index.json" : "world-v1-index.json"),
    Get("model-answers") ?? Answers("model-lane-v2.json", "model-lane-low.json"),
    Get("embeddings") ?? Path.Combine(runs, set == "v2" ? "embeddings-v2.json" : "embeddings.json"),
    Get("home") ?? Path.Combine(strategy, "memoryrouter", "arena"),
    // Every recorded way of asking a decision model (jev-doors.json, jev-items.json, jev-items-k12.json, …), by its name.
    set == "v2" && Directory.Exists(runs)
        ? Directory.GetFiles(runs, "jev-*.json").ToDictionary(f => Path.GetFileNameWithoutExtension(f)["jev-".Length..], f => f)
        : null);
// 40.77, 40.78: a fresh set (v3, v4, …): its items, its frozen matrix, its folder of recordings and answers.
var freshSet = Get("fresh-set") ?? "v3";
string FreshDir() => Path.Combine(runs, freshSet == "v3" ? "fresh" : $"fresh-{freshSet}");
string FreshItems() => Path.Combine(fixtures, $"matrix-{freshSet}-items.json");
string FreshFrozen() => Get("fresh-matrix") ?? Path.Combine(fixtures, $"routing-matrix-{freshSet}-fresh.json");
int? Limit() => Get("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : null;
var dryRun = args.Contains("--dry-run");

switch (args.FirstOrDefault())
{
    case "record":
        if (Get("world") is not { } world || Get("items") is not { } items || Get("out") is not { } output)
        {
            Console.WriteLine("router-arena record --world <pack> --items <matrix> --out <recording> [--variants \"0.65:1,0.6:2\"]");
            return 1;
        }
        return await new Recorder(Console.Out).RunAsync(new RecordRequest(world, items, output, RecordRequest.ParseVariants(Get("variants"))));
    case "ask-model":
    {
        var paths = Paths();
        return await new ModelAsk(Console.Out).RunAsync(Matrix.Read(paths.Matrix), Get("out") ?? Path.Combine(runs, "model-lane-v2.json"),
            dryRun, Limit(), int.Parse(Get("workers") ?? "4", CultureInfo.InvariantCulture));
    }
    case "ask-jev":
    {
        var paths = Paths();
        var mode = Get("mode") ?? "doors";
        var askOptions = new JevAskOptions
        {
            Shown = int.Parse(Get("shown") ?? JevAsk.ShownPerDoor.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            Context = args.Contains("--context"),
            Examples = int.Parse(Get("examples") ?? "0", CultureInfo.InvariantCulture),
            Single = args.Contains("--single"),
            Split = Get("split") ?? "all",
            Endpoint = Get("endpoint") ?? JevClient.TypeSafeEndpoint,
            KeyVariable = Get("endpoint") is { } e && new Uri(e).IsLoopback ? null : "TYPESAFE_API_KEY",
            DumpRequests = Get("dump-requests"),
        };
        var askPaths = paths with { Jev = null };
        if (args.Contains("--fresh"))
            askPaths = askPaths with
            {
                Matrix = FreshItems(),
                Recording = Path.Combine(FreshDir(), "recording-fresh.json"),
                Embeddings = Path.Combine(FreshDir(), "embeddings-merged.json"),
                TrainMatrix = paths.Matrix,
            };
        return await new JevAsk(Console.Out).RunAsync(ArenaData.Load(askPaths), mode, Get("out") ?? Path.Combine(runs, $"jev-{mode}.json"),
            dryRun, Limit(), int.Parse(Get("workers") ?? "4", CultureInfo.InvariantCulture), Get("model") ?? "jev-latest", askOptions);
    }
    case "round1":
    {
        var paths = Paths();
        var rounds = new Rounds(ArenaData.Load(paths), paths.Home, Console.Out);
        rounds.Run(Get("name") ?? "round1", "dev", rounds.Round1Roster(), "Round 1: every contestant on the tuning turns, untuned.");
        return 0;
    }
    case "round2":
    {
        var paths = Paths();
        var rounds = new Rounds(ArenaData.Load(paths), paths.Home, Console.Out);
        rounds.Run(Get("name") ?? "round2", "dev", rounds.Round2Roster(), "Round 2: tuned on the tuning turns, under the champion's noise; crosses.");
        return 0;
    }
    case "round4":
    {
        var paths = Paths();
        var rounds = new Rounds(ArenaData.Load(paths), paths.Home, Console.Out);
        IReadOnlySet<string>? only = Get("answers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        rounds.Run(Get("name") ?? "round4", "dev", rounds.Round4Roster(only), "Round 4 (40.75): the gate per memory improved on the tuning turns: thresholds per door, a cap per door, quiet, a time judge; every recorded way of asking.");
        return 0;
    }
    case "time":
    {
        // 40.73: every contestant timed end to end against the matrix world on a throwaway store.
        var paths = Paths();
        var data = ArenaData.Load(paths);
        var rounds = new Rounds(data, paths.Home, Console.Out);
        var roster = rounds.TimeRoster();
        if (Get("only") is { } only) roster = roster.Where(r => r.Key.Contains(only, StringComparison.OrdinalIgnoreCase)).ToDictionary();
        var request = new RecordRequest(Get("world") ?? Path.Combine(fixtures, "world.pack.json"), paths.Matrix, "", []);
        return await new AgentMemory.RouterArena.Record.Timer(Console.Out).RunAsync(data, roster, request,
            Get("out") ?? Path.Combine(runs, $"time-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"), Get("split") ?? "dev", Limit());
    }
    case "final-fresh":
    {
        // 40.77: tuned on the v2 tuning turns (as round 4 did), scored once on the fresh turns.
        var tuningPaths = Paths();
        var tuning = new Rounds(ArenaData.Load(tuningPaths), tuningPaths.Home, Console.Out);
        var tuned = tuning.Round2Roster().Concat(tuning.Round4Roster(new HashSet<string> { "items-k12ctx" })).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().Value);
        var roster = new Dictionary<string, IContestant>
        {
            ["C00 oracle"] = new AgentMemory.RouterArena.Contestants.Oracle(), ["C01w search everything, wide"] = new AgentMemory.RouterArena.Contestants.WideOpen(),
            ["C01 search everything (today)"] = new AgentMemory.RouterArena.Contestants.OldMethod(),
        };
        foreach (var name in Final.FreshFinalists) roster[name] = tuned[name];
        var round = Get("name") ?? (freshSet == "v3" ? "final-fresh" : $"final-fresh-{freshSet}");
        if (freshSet != "v3")
        {
            // 40.78, written before the v4 turns were labelled, recorded or asked about: the gates as selected after the
            // v3 read and the local-gate work — JEV online in its plain form, the hybrid (local first, JEV on the unsure
            // types), the local gate alone — beside the survivor, the no-model fallback and today.
            roster = new Dictionary<string, IContestant>
            {
                ["C00 oracle"] = roster["C00 oracle"], ["C01 search everything (today)"] = roster["C01 search everything (today)"],
                ["C20t JEV gate per memory, tuned"] = roster["C20t JEV gate per memory, tuned"],
                ["C06t door gates (before search)"] = roster["C06t door gates (before search)"],
                ["C22 JEV gate per memory, plain (12 shown, context, examples, 0.25)"] =
                    new AgentMemory.RouterArena.Contestants.JevItems(new AgentMemory.RouterArena.Contestants.JevItemSettings { Threshold = 0.25, Answers = "items-k12ctx" }),
                ["C23 hybrid: local gate first, JEV on the unsure types"] = new AgentMemory.RouterArena.Contestants.GateCascade("items-xenc-mini-acc", "items-k12ctx", 0.2, 0.8, 0.25),
                ["C24 local gate alone (cross-encoder, 0.279)"] =
                    new AgentMemory.RouterArena.Contestants.JevItems(new AgentMemory.RouterArena.Contestants.JevItemSettings { Threshold = 0.2787, Answers = "items-xenc-mini-acc" }),
                ["C24g local gate alone, generous (cross-encoder, 0.213)"] =
                    new AgentMemory.RouterArena.Contestants.JevItems(new AgentMemory.RouterArena.Contestants.JevItemSettings { Threshold = 0.2129, Answers = "items-xenc-mini-acc" }),
            };
        }
        if (args.Contains("--explore"))
        {
            // Exploratory, chosen after the fresh read: what was improved on dev, without the per-door tuning that did not carry
            // over. Logged under its own round name; claimed only after another unseen set.
            roster = new Dictionary<string, IContestant>
            {
                ["C01 search everything (today)"] = roster["C01 search everything (today)"],
                ["C20t JEV gate per memory, tuned"] = roster["C20t JEV gate per memory, tuned"],
                ["C22 gate per memory: 12 shown, context, examples, one threshold 0.25 (exploratory)"] =
                    new AgentMemory.RouterArena.Contestants.JevItems(new AgentMemory.RouterArena.Contestants.JevItemSettings { Threshold = 0.25, Answers = "items-k12ctx" }),
                ["C22 gate per memory: 12 shown, context, examples, one threshold 0.2 (exploratory)"] =
                    new AgentMemory.RouterArena.Contestants.JevItems(new AgentMemory.RouterArena.Contestants.JevItemSettings { Threshold = 0.2, Answers = "items-k12ctx" }),
            };
            round = Get("name") ?? "final-fresh-exploratory";
        }
        var fresh = FreshDir();
        var freshPaths = tuningPaths with
        {
            Matrix = FreshFrozen(),
            Recording = Path.Combine(fresh, "recording-fresh.json"),
            Embeddings = Path.Combine(fresh, "embeddings-merged.json"),
            Jev = Directory.GetFiles(fresh, "jev-*.json").ToDictionary(f => Path.GetFileNameWithoutExtension(f)["jev-".Length..], f => f),
            TrainMatrix = tuningPaths.Matrix,
        };
        new Rounds(ArenaData.Load(freshPaths), tuningPaths.Home, Console.Out).Run(round, "all", roster,
            args.Contains("--explore") ? "EXPLORATORY: chosen after the fresh read; not a claim. " + Final.FreshRule : Final.FreshRule);
        return 0;
    }
    case "final":
    {
        var paths = Paths();
        var rounds = new Rounds(ArenaData.Load(paths), paths.Home, Console.Out);
        rounds.Run(Get("name") ?? "final", "heldout", rounds.FinalRoster(), Final.Rule);
        return 0;
    }
    case "candidates":
    {
        // For analysis: every turn's candidates per door, wide and as shipped, with the referee's own id mapping.
        var data = ArenaData.Load(Paths() with { Jev = null });
        var dump = new Dictionary<string, object>();
        foreach (var item in data.Matrix.Items)
        {
            var context = new TurnContext(data.Records[item.Id], data);
            object Rows(IEnumerable<Candidate> cs) => cs.Select(c => new object?[] { c.Id, Doors.Name(c.Door), c.Score, c.Rank }).ToList();
            dump[item.Id] = new Dictionary<string, object>
            {
                ["wide"] = Rows(context.Source(false, wide: true)),
                ["today"] = Rows(context.Source(false, wide: false)),
                ["reading"] = Rows(Doors.Reading.SelectMany(context.Reading)),
                ["shown"] = Doors.All.ToDictionary(Doors.Name, d => JevAsk.Shown(d, context).Select(s => s.Id).ToList()),
            };
        }
        var outPath = Get("out") ?? Path.Combine(runs, "candidates-v4.json");
        File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(dump));
        Console.WriteLine($"candidates: {dump.Count} turns to {outPath}");
        return 0;
    }
    case "gate-pairs":
    {
        // 40.79, 40.80: every memory a gate is shown (twelve per door, with its dates), with what the code knows about it
        // (score, rank, door, the nearest examples' vote, whether today's recall keeps it) and its label; one JSON per line.
        var paths = Paths() with { Jev = null };
        if (args.Contains("--fresh"))
            paths = paths with
            {
                Matrix = FreshFrozen(),
                Recording = Path.Combine(FreshDir(), "recording-fresh.json"),
                Embeddings = Path.Combine(FreshDir(), "embeddings-merged.json"),
                TrainMatrix = Paths().Matrix,
            };
        var data = ArenaData.Load(paths);
        var lines = new List<string>();
        foreach (var item in data.Matrix.Items)
        {
            var context = new TurnContext(data.Records[item.Id], data);
            var neighbours = context.Neighbours(item, 9, rewrite: false);
            var weight = Math.Max(1e-9, neighbours.Sum(n => Math.Max(n.Similarity, 0)));
            var today = context.Source(false, wide: false).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var scores = context.Source(false, wide: true).GroupBy(c => (c.Door, c.Id)).ToDictionary(g => g.Key, g => g.First());
            var needed = item.Needs.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
            var doorsNeeded = Referee.DoorsNeeded(item, data, data.Matrix.DoorsLabelled);
            var cued = AgentMemory.RouterArena.Contestants.RulesV2.Cued(item);
            foreach (var door in Doors.All)
            {
                var share = neighbours.Where(n => Referee.DoorsNeeded(n.Example, data, data.Matrix.DoorsLabelled).Contains(door)).Sum(n => Math.Max(n.Similarity, 0)) / weight;
                var shown = JevAsk.Shown(door, context, 12, contextual: true);
                for (var i = 0; i < shown.Count; i++)
                {
                    var (id, text) = shown[i];
                    scores.TryGetValue((door, id), out var candidate);
                    lines.Add(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        turn = item.Id, split = Matrix.SplitOf(item), lang = item.Lang, category = item.Category, text = item.Text,
                        prior = item.Prior.Select(p => $"{p.Role}: {p.Text}").ToList(), smallTalk = AgentMemory.RouterArena.Contestants.RulesV2.IsSmallTalk(item.Text),
                        door = Doors.Name(door), cued = cued.Contains(door), doorShare = Math.Round(share, 3), doorNeeded = doorsNeeded.Contains(door),
                        doorAcceptable = item.DoorsAcceptable.Contains(door), memory = id, memoryText = text, rank = i + 1,
                        score = candidate?.Score, inToday = today.Contains(id),
                        label = needed.Contains(id) ? "needed" : item.Acceptable.Contains(id) ? "acceptable" : "none",
                    }));
                }
            }
        }
        var pairsOut = Get("out") ?? (args.Contains("--fresh") ? Path.Combine(FreshDir(), "gate-pairs-fresh.jsonl") : Path.Combine(runs, "gate-pairs-v2.jsonl"));
        File.WriteAllLines(pairsOut, lines);
        Console.WriteLine($"gate-pairs: {lines.Count} pairs from {data.Matrix.Items.Count} turns to {pairsOut}");
        return 0;
    }
    case "leaderboard":
        RunLog.RenderLeaderboard(Paths().Home);
        return 0;
    case "inspect" when args.Length > 1:
        var matrix = Matrix.Read(args[1]);
        Console.WriteLine($"matrix: {matrix.Items.Count} turns, dev {matrix.InSplit("dev").Count()}, held out {matrix.InSplit("heldout").Count()}, sha256 {matrix.Sha256[..12]}");
        return 0;
    default:
        Console.WriteLine("router-arena record | ask-model | ask-jev --mode doors|gates|items | round1 | round2 | final | leaderboard | inspect  [--set v1|v2] [--dry-run] [--limit n]");
        return args.Length == 0 ? 0 : 1;
}
