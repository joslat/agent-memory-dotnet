using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Routing;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Arena;

/// <summary>
/// One recalled item as a contestant sees it: its memory id, the door it came through, its section, score (null when it
/// has none) and rank.
/// </summary>
public sealed record Candidate(string Id, Door Door, RecallSection Section, double? Score, int Rank);

/// <summary>
/// What a contestant decided for one turn: the memory ids it let into the prompt, the doors it opened (any number), the
/// searches it ran, the model calls it made, and which lane it took. The referee scores this, never the contestant's own
/// account. A router that decides before the search opens doors and searches them; a gate after the search searches every
/// door and opens the ones whose gate let something in.
/// </summary>
public sealed record Decision(IReadOnlySet<string> Admitted, IReadOnlySet<Door> Opened, int Searches, int ModelCalls = 0, string Lane = "one")
{
    /// <summary>The model time the decision took when it was measured (JEV's recorded answers); else the referee's per-call median.</summary>
    public double? ModelSeconds { get; init; }

    /// <summary>
    /// 40.73: whether the doors were searched wide open (a gate after the search judges what came back) rather than at the
    /// shipped floor and caps, and which doors were searched when that is not the doors opened. <c>time</c> runs exactly
    /// this search.
    /// </summary>
    public bool Wide { get; init; }

    public IReadOnlySet<Door>? SearchedDoors { get; init; }

    public static Decision Nothing(int searches = 0, int model = 0, string lane = "one") =>
        new(new HashSet<string>(), new HashSet<Door>(), searches, model, lane);

    /// <summary>A decision before the search: the doors opened are the doors searched (the working door costs no search).</summary>
    public static Decision Of(IEnumerable<Candidate> admitted, IEnumerable<Door> opened, int model = 0, string lane = "one")
    {
        var doors = opened.ToHashSet();
        return new(admitted.Select(c => c.Id).ToHashSet(StringComparer.Ordinal), doors, Doors.SearchesOf(doors), model, lane);
    }
}

/// <summary>
/// An insect in the jar: one routing idea. Every contestant implements this one interface and lives in its own folder
/// under <c>Contestants/</c>; crossing two of them is composition.
/// </summary>
public interface IContestant
{
    /// <summary>Its family (champion, rules, gates, examples, model, lanes, rewrite, pool, cross, bound): the leaderboard groups by it.</summary>
    string Family { get; }

    /// <summary>Its parameters, written into every run log so a result names exactly what produced it.</summary>
    IReadOnlyDictionary<string, object?> Parameters { get; }

    Decision Decide(MatrixItem item, TurnContext context);
}

/// <summary>What a contestant may draw on for one turn: the turn's recorded recalls, as candidates, and the arena's data.</summary>
public sealed class TurnContext(TurnRecord record, ArenaData data)
{
    private readonly Dictionary<string, IReadOnlyList<Candidate>> _cache = new(StringComparer.Ordinal);

    public TurnRecord Record { get; } = record;

    public ArenaData Data { get; } = data;

    /// <summary>
    /// The searching doors' recall a contestant draws on: as shipped or wide open, of the turn or (rewrite, contestant 10)
    /// of the turn with the one before. A rewritten turn was recorded wide only; its shipped form is the shipped cut of that.
    /// </summary>
    public IReadOnlyList<Candidate> Source(bool rewrite, bool wide)
    {
        var rewritten = rewrite && Record.WithPrior is not null;
        return Cached($"{rewritten}:{wide}", () => rewritten
            ? wide ? Candidates.Of(Data.World, Record.WithPrior!) : Candidates.TodayCut(Data.World, Record.WithPrior!, Data.Recording.Today)
            : Candidates.Of(Data.World, wide ? Record.Wide : Record.Today));
    }

    /// <summary>
    /// What a reading door lets in when it is opened, as recorded at the shipped setting: <see cref="Door.Temporal"/> the
    /// facts read as currently valid; <see cref="Door.BiTemporal"/> the facts as they were at each recorded date;
    /// <see cref="Door.Prospective"/> what is due or expiring; <see cref="Door.Forgetting"/> what has faded. A recording
    /// without the doors has nothing behind them.
    /// </summary>
    public IReadOnlyList<Candidate> Reading(Door door) => Cached($"door:{door}", () =>
    {
        var doors = Record.Doors;
        if (doors is null) return [];
        IReadOnlyList<Candidate> Facts(Section? section, Door through) =>
            section is null ? [] : [.. Candidates.Of(Data.World, section, through).Where(c => c.Section is RecallSection.Facts)];
        return door switch
        {
            Door.Temporal => Facts(doors.GetValueOrDefault("temporal"), Door.Temporal),
            // 40.91: the replaced facts behind the found ones ("history") beside the readings as of fixed dates.
            Door.BiTemporal => [.. doors.Where(d => d.Key.StartsWith("asOf:", StringComparison.Ordinal) || d.Key == "history").OrderBy(d => d.Key, StringComparer.Ordinal)
                .SelectMany(d => Facts(d.Value, Door.BiTemporal)).DistinctBy(c => c.Id)],
            Door.Prospective => doors.GetValueOrDefault("prospective") is { } p
                ? [.. Candidates.Of(Data.World, p, Door.Prospective).Where(c => c.Section is RecallSection.Due or RecallSection.Expiring)]
                : [],
            Door.Forgetting => Facts(doors.GetValueOrDefault("faded"), Door.Forgetting),
            _ => [],
        };
    });

    /// <summary>
    /// Opens the chosen doors before the search, as the shipped recall would read them: each searching door at the
    /// shipped floor and caps; a reading door as recorded. The temporal door reads the facts as currently valid, so with
    /// it open the semantic and derived facts come from that reading. The working door (the profile and the last
    /// messages) is never scored as a memory: a router must not lean on a short conversation.
    /// </summary>
    public Decision Open(IEnumerable<Door> doors, bool rewrite = false, int model = 0, string lane = "one")
    {
        var open = doors.ToHashSet();
        var admitted = new List<Candidate>();
        var current = open.Contains(Door.Temporal) && Record.Doors?.ContainsKey("temporal") == true
            ? Reading(Door.Temporal).Select(c => Data.World.DoorOf(c.Id) == Door.Derived ? c with { Door = Door.Derived } : c).ToList()
            : null;
        foreach (var candidate in Source(rewrite, wide: false))
        {
            if (current is not null && candidate.Section == RecallSection.Facts) continue;
            if (open.Contains(candidate.Door)) admitted.Add(candidate);
        }
        if (current is not null) admitted.AddRange(current.Where(c => open.Contains(c.Door)));
        foreach (var door in Doors.Reading.Where(d => d != Door.Temporal && open.Contains(d))) admitted.AddRange(Reading(door));
        return Decision.Of(admitted, open, model, lane);
    }

/// <summary>A recall recorded at another setting (the old method re-tuned), or with fan-out off.</summary>
    public IReadOnlyList<Candidate> Recorded(string name, Section section) => Cached(name, () => Candidates.Of(Data.World, section));

    /// <summary>The k nearest labelled turns of the training split (this turn left out), by cosine of their embeddings.</summary>
    public IReadOnlyList<(double Similarity, MatrixItem Example)> Neighbours(MatrixItem item, int k, bool rewrite)
    {
        var key = rewrite && item.Prior.Count > 0 ? "withPrior" : "text";
        var vector = Data.Embeddings[item.Id][key];
        return [.. Data.Train.Where(e => e.Id != item.Id)
            .Select(e => (Similarity: Cosine(vector, Data.Embeddings[e.Id]["text"]), Example: e))
            .OrderByDescending(s => s.Similarity)
            .Take(k)];
    }

    private IReadOnlyList<Candidate> Cached(string key, Func<IReadOnlyList<Candidate>> make)
    {
        if (!_cache.TryGetValue(key, out var list)) _cache[key] = list = make();
        return list;
    }

    internal static double Cosine(double[] a, double[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var norm = Math.Sqrt(na) * Math.Sqrt(nb);
        return dot / (norm == 0 ? 1 : norm);
    }
}

/// <summary>Everything a fight reads: the frozen matrix, the recording, the world, the recorded model answers and embeddings.</summary>
public sealed class ArenaData
{
    public required Matrix Matrix { get; init; }
    public required Recording Recording { get; init; }
    public required string RecordingSha256 { get; init; }
    public required WorldIndex World { get; init; }

    /// <summary>40.92: the world the training turns' memories belong to (another world's turns learn from world 1's examples).</summary>
    public WorldIndex? TrainWorld { get; init; }

    public required IReadOnlyDictionary<string, TurnRecord> Records { get; init; }

    /// <summary>The model lane's recorded answers by turn: its doors (or, asked before the doors, its kinds), or null when the call failed.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>?> ModelAnswers { get; init; }

    /// <summary>JEV's recorded answers by way of asking ("doors", "gates", "items"), then by turn; null when the turn's calls failed.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JevAnswer?>> Jev { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, JevAnswer?>>();

    /// <summary>Each turn's embedding ("text", and "withPrior" for the turn with the one before).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, double[]>> Embeddings { get; init; }

    /// <summary>The training split's turns (the nearest-examples contestants learn from them).</summary>
    public required IReadOnlyList<MatrixItem> Train { get; init; }

    /// <summary>The core rule router as shipped (contestant 2 calls it, not a copy of its rules).</summary>
    public RuleBasedMemoryRouter Rules { get; } = new(new MemoryRoutingOptions());

    public required IReadOnlyDictionary<string, string> InputShas { get; init; }

    public static ArenaData Load(ArenaPaths paths)
    {
        var matrix = Matrix.Read(paths.Matrix);
        var (recording, recordingSha) = Recording.ReadFile(paths.Recording);
        var world = WorldIndex.Read(paths.WorldIndex, paths.Owner);
        var answers = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
        using (var model = JsonDocument.Parse(File.ReadAllBytes(paths.ModelAnswers)))
            foreach (var answer in model.RootElement.GetProperty("answers").EnumerateObject())
                answers[answer.Name] = answer.Value.TryGetProperty("doors", out var doors) || answer.Value.TryGetProperty("kinds", out doors)
                    ? [.. doors.EnumerateArray().Select(k => k.GetString()!)]
                    : null;
        var embeddings = new Dictionary<string, IReadOnlyDictionary<string, double[]>>(StringComparer.Ordinal);
        using (var vectors = JsonDocument.Parse(File.ReadAllBytes(paths.Embeddings)))
            foreach (var turn in vectors.RootElement.EnumerateObject())
                embeddings[turn.Name] = turn.Value.EnumerateObject().ToDictionary(
                    v => v.Name, v => v.Value.EnumerateArray().Select(x => x.GetDouble()).ToArray(), StringComparer.Ordinal);
        var jev = new Dictionary<string, IReadOnlyDictionary<string, JevAnswer?>>(StringComparer.Ordinal);
        var shas = new Dictionary<string, string>
        {
            ["matrix"] = matrix.Sha256, ["recording"] = recordingSha,
            ["modelAnswers"] = Sha(paths.ModelAnswers), ["embeddings"] = Sha(paths.Embeddings), ["worldIndex"] = Sha(paths.WorldIndex),
        };
        foreach (var (mode, path) in paths.Jev ?? new Dictionary<string, string>())
        {
            if (!File.Exists(path)) continue;
            jev[mode] = JevAnswer.Read(path);
            shas[$"jev-{mode}"] = Sha(path);
        }
        return new ArenaData
        {
            Matrix = matrix,
            Recording = recording,
            RecordingSha256 = recordingSha,
            World = world,
            TrainWorld = paths.TrainWorldIndex is { } trainWorld ? WorldIndex.Read(trainWorld) : null,
            Records = recording.Records.ToDictionary(r => r.Id, StringComparer.Ordinal),
            ModelAnswers = answers,
            Embeddings = embeddings,
            // The examples a contestant learns from: the tuning turns of this matrix, or of the matrix tuned on (a fresh set).
            Train = [.. (paths.TrainMatrix is { } train ? Matrix.Read(train) : matrix).InSplit("dev")],
            Jev = jev,
            InputShas = shas,
        };
    }

    private static string Sha(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

/// <summary>Where a fight reads from and writes to; <paramref name="Jev"/>: JEV's recorded answers by way of asking, when there are any.</summary>
public sealed record ArenaPaths(string Matrix, string Recording, string WorldIndex, string ModelAnswers, string Embeddings, string Home,
    IReadOnlyDictionary<string, string>? Jev = null)
{
    /// <summary>40.77: on a fresh set, the matrix whose tuning turns are the contestants' examples (the set they were tuned on).</summary>
    public string? TrainMatrix { get; init; }

    /// <summary>40.92: the index of the world the training matrix belongs to, when it is not this one's.</summary>
    public string? TrainWorldIndex { get; init; }

    /// <summary>40.92: the world's person (a second world names its own: --owner sven).</summary>
    public string Owner { get; init; } = "marta";
}

/// <summary>JEV's answer for one turn: P(yes) by question key (a door, or "door|memory id"), its calls, time and cost.</summary>
public sealed record JevAnswer(IReadOnlyDictionary<string, double> Yes, int Calls, double Seconds, double Cost)
{
    public static IReadOnlyDictionary<string, JevAnswer?> Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var answers = new Dictionary<string, JevAnswer?>(StringComparer.Ordinal);
        foreach (var turn in document.RootElement.GetProperty("answers").EnumerateObject())
            answers[turn.Name] = turn.Value.TryGetProperty("yes", out var yes)
                ? new JevAnswer(yes.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal),
                    turn.Value.GetProperty("calls").GetInt32(), turn.Value.GetProperty("seconds").GetDouble(), turn.Value.GetProperty("cost").GetDouble())
                : null;
        return answers;
    }
}

/// <summary>Turning recorded recalls into candidates.</summary>
public static partial class Candidates
{
    private static readonly RecallSection[] Order =
    [
        RecallSection.Facts, RecallSection.Entities, RecallSection.Relationships, RecallSection.Preferences,
        RecallSection.RelevantMessages, RecallSection.Traces, RecallSection.Due, RecallSection.Expiring,
    ];

    [GeneratedRegex(@"^(.*) -\[(.*)\]-> (.*)$")]
    private static partial Regex Relationship();

    private static IReadOnlyList<Hit> Hits(Section s, RecallSection section) => section switch
    {
        RecallSection.Facts => s.Facts, RecallSection.Entities => s.Entities, RecallSection.Relationships => s.Relationships,
        RecallSection.Preferences => s.Preferences, RecallSection.RelevantMessages => s.RelevantMessages,
        RecallSection.Traces => s.Traces, RecallSection.Due => s.Due, RecallSection.Expiring => s.Expiring, _ => [],
    };

    /// <summary>
    /// Every recalled item, mapped to ids, each with the door it came through: <paramref name="through"/> when a reading
    /// door read it, else the door that holds it (a fact the accountant computed is derived, a trace procedural or
    /// reasoning by its kind). A relationship has no score of its own: it gets the best score of the people it joins that
    /// were recalled with it (it is recalled because of them).
    /// </summary>
    public static IReadOnlyList<Candidate> Of(WorldIndex world, Section section, Door? through = null)
    {
        var people = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var e in section.Entities) people[WorldIndex.Norm(e.Key)] = e.Score;
        var output = new List<Candidate>();
        foreach (var recall in Order)
        {
            foreach (var hit in Hits(section, recall))
            {
                var id = world.IdOf(recall, hit);
                var score = hit.Score;
                if (recall == RecallSection.Relationships)
                {
                    var match = Relationship().Match(hit.Key);
                    var ends = match.Success
                        ? new[] { people.GetValueOrDefault(WorldIndex.Norm(match.Groups[1].Value)), people.GetValueOrDefault(WorldIndex.Norm(match.Groups[3].Value)) }
                            .Where(v => v is not null).Select(v => v!.Value).ToList()
                        : [];
                    score = ends.Count > 0 ? ends.Max() : null;
                }
                var door = through ?? recall switch
                {
                    RecallSection.Entities or RecallSection.Relationships => Door.EntityGraph,
                    RecallSection.Preferences => Door.Preference,
                    RecallSection.RelevantMessages => Door.Episodic,
                    RecallSection.Traces => hit.Kind == "procedure" ? Door.Procedural : Door.Reasoning,
                    RecallSection.Due or RecallSection.Expiring => Door.Prospective,
                    _ => hit.Kind == "derived" ? Door.Derived : Door.Semantic,
                };
                output.Add(new Candidate(id, door, recall, score, hit.Rank));
            }
        }
        return output;
    }

    /// <summary>
    /// What the shipped recall keeps of a wide recall: per section, items at or above the floor, up to the cap; the
    /// relationships of the people kept (the shipped recall expands the people it kept, not every person a wide search saw).
    /// </summary>
    public static IReadOnlyList<Candidate> TodayCut(WorldIndex world, Section section, Caps caps)
    {
        IReadOnlyList<Hit> Cut(IReadOnlyList<Hit> hits, int cap) =>
            [.. hits.Where(h => h.Score is null || h.Score >= caps.MinSimilarity).Take(cap)];
        var entities = Cut(section.Entities, caps.Entities);
        var kept = entities.Select(e => WorldIndex.Norm(e.Key)).ToHashSet(StringComparer.Ordinal);
        var relationships = section.Relationships.Where(h =>
        {
            var m = Relationship().Match(h.Key);
            return m.Success && (kept.Contains(WorldIndex.Norm(m.Groups[1].Value)) || kept.Contains(WorldIndex.Norm(m.Groups[3].Value)));
        }).Take(caps.Relationships).ToList();
        return Of(world, new Section(Cut(section.Facts, caps.Facts), entities, relationships, Cut(section.Preferences, caps.Preferences),
            Cut(section.RelevantMessages, caps.RelevantMessages), [], null) { Traces = Cut(section.Traces, caps.Traces) });
    }
}
