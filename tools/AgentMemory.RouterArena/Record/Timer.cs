using System.Diagnostics;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Contestants;
using AgentMemory.RouterArena.Data;
using AgentMemory.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.RouterArena.Record;

/// <summary>One contestant's time per turn: the recall its decision implies, run for real, and its model time.</summary>
public sealed record TimeScore(string Contestant, double RecallP50, double RecallP95, double ModelP50, double TotalP50, double TotalP95, double TotalMean, int Turns);

/// <summary>
/// <c>time</c> (root PLAN 40.73): how fast each contestant is, end to end. Every contestant decides every turn as in the
/// fight (from the recording); then the recall that decision implies is run for real against the matrix world on a
/// throwaway store: the doors it opened at the shipped floor and caps, or, for a gate after the search, every door it
/// searched wide open; its reading doors beside (valid now, as of each date, what is due, what faded), all at once as a
/// router would fire them. Its model time is the recorded one (JEV's slowest call per turn, a local model's) or, for the
/// model lane, the measured median per call. The turn waits for both: model, then recall (a judge before the search) or
/// recall, then model (a gate after it).
/// </summary>
public sealed class Timer(TextWriter output)
{
    /// <summary>Fan-out on for every contestant (as the recordings were made); off: only today's recall keeps it (the gate's design).</summary>
    public bool FanOutForAll { get; init; } = true;

    /// <summary>40.83: time each door alone instead of the contestants: its own search (wide), or its own reading.</summary>
    public bool DoorsOnly { get; init; }

    public async Task<int> RunAsync(ArenaData data, IReadOnlyDictionary<string, IContestant> roster, RecordRequest request, string outPath,
        string split, int? limit, CancellationToken cancellationToken = default)
    {
        var world = ValidationPackReader.ReadFile(request.WorldPath);
        await using var matrix = await MatrixStore.StartAsync(world, request, output, "time", cancellationToken).ConfigureAwait(false);
        if (matrix is null) return 1;
        var store = matrix.Store;
        var shipped = store.Options.Recall;
        var fanOutShipped = store.Options.FanOut.Enabled;
        var items = data.Matrix.InSplit(split).Take(limit ?? int.MaxValue).ToList();
        var contexts = new Dictionary<string, TurnContext>(StringComparer.Ordinal);

        // Warm the store and the embedder on every turn, wide and fan-out on, so no contestant pays for cold caches: timed
        // first, today's recall once paid 88 ms against 24 ms warm (40.83).
        foreach (var item in items)
            await store.RecallAsync(request.Owner, request.Session, item.Text, request.AskedAt, _ => Recorder.WideOpen(shipped), cancellationToken: cancellationToken).ConfigureAwait(false);
        // The faded search takes the turn's vector, which the recall has already embedded (and the library caches): embed
        // each text once here, not once per contestant (that cost ~70 ms a turn the library never pays).
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var item in items)
            if (!vectors.ContainsKey(item.Text))
                vectors[item.Text] = (await matrix.Embeddings.GenerateAsync([item.Text], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.ToArray();

        var scores = new List<TimeScore>();
        // 40.83: a door alone is a contestant that opens and searches only that door (wide), fan-out off.
        IEnumerable<(string Name, IContestant Contestant)> timed = DoorsOnly
            ? Doors.All.Select(d => ($"door {Doors.Name(d)}", (IContestant)new OneDoor(d)))
            : roster.Select(r => (r.Key, r.Value));
        foreach (var (name, contestant) in timed)
        {
            store.Options.FanOut.Enabled = contestant is WithoutFanOut || contestant is OneDoor ? false
                : contestant is OldMethod || FanOutForAll ? fanOutShipped : false;
            var recall = new List<double>();
            var model = new List<double>();
            foreach (var item in items)
            {
                if (!contexts.TryGetValue(item.Id, out var context)) contexts[item.Id] = context = new TurnContext(data.Records[item.Id], data);
                var decision = contestant.Decide(item, context);
                var watch = Stopwatch.StartNew();
                await RecallAsync(store, shipped, request, item.Text, vectors[item.Text], decision, contestant, cancellationToken).ConfigureAwait(false);
                recall.Add(watch.Elapsed.TotalMilliseconds);
                model.Add(1000 * (decision.ModelSeconds ?? decision.ModelCalls * Referee.ModelSeconds));
            }
            var total = recall.Zip(model, (r, m) => r + m).ToList();
            var score = new TimeScore(name, P(recall, 50), P(recall, 95), P(model, 50), P(total, 50), P(total, 95), Math.Round(total.Average(), 1), items.Count);
            scores.Add(score);
            output.WriteLine($"  {name,-52} recall p50 {score.RecallP50,7:0.0} ms  p95 {score.RecallP95,7:0.0}  model p50 {score.ModelP50,7:0}  total p50 {score.TotalP50,7:0} p95 {score.TotalP95,7:0}");
        }
        store.Options.FanOut.Enabled = fanOutShipped;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new
        {
            format = "router-time/1", split, turns = items.Count, at = DateTimeOffset.UtcNow, machine = Environment.MachineName,
            store = $"{Recorder.Image}, heap 512m, page cache 128m, Testcontainers", embeddings = $"{request.Model} via Ollama", inputs = data.InputShas, scores,
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        output.WriteLine($"time: {scores.Count} contestants × {items.Count} turns to {outPath}");
        return 0;
    }

    /// <summary>The recall a decision implies, run for real: one main recall, and the reading doors beside it, all at once.</summary>
    private static Task RecallAsync(PackStore store, RecallOptions shipped, RecordRequest request, string text, float[] vector,
        Decision decision, IContestant contestant, CancellationToken cancellationToken)
    {
        if (contestant is OldMethod or WithoutFanOut) return store.RecallAsync(request.Owner, request.Session, text, request.AskedAt, _ => shipped, cancellationToken: cancellationToken);
        var doors = decision.SearchedDoors ?? decision.Opened;
        var wide = decision.Wide;
        var main = Options(shipped, doors, wide);
        var mainRecall = store.RecallAsync(request.Owner, request.Session, text, request.AskedAt, _ => main, cancellationToken: cancellationToken);
        // 40.91: the bi-temporal door also reads the facts the found ones replaced, once the search has found them.
        var calls = new List<Task> { doors.Contains(Door.BiTemporal)
            ? mainRecall.ContinueWith(r => Recorder.HistoryAsync(store, r.Result, cancellationToken), cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default).Unwrap()
            : mainRecall };
        if (wide && doors.Contains(Door.Temporal))
            calls.Add(store.RecallAsync(request.Owner, request.Session, text, request.AskedAt, _ => Recorder.WideOpen(shipped) with { ValidTime = ValidTimeMode.Current }, cancellationToken: cancellationToken));
        if (doors.Contains(Door.Prospective))
            calls.Add(store.RecallAsync(request.Owner, request.Session, text, request.AskedAt, _ => shipped with { ValidTime = ValidTimeMode.Current, ProspectiveFiring = true }, cancellationToken: cancellationToken));
        if (doors.Contains(Door.BiTemporal))
            foreach (var then in request.AsOfDates)
                calls.Add(store.RecallAsync(request.Owner, request.Session, text, request.AskedAt, _ => shipped, new PackAsOf { Valid = then, System = request.AskedAt }, cancellationToken));
        if (doors.Contains(Door.Forgetting)) calls.Add(FadedAsync(store, shipped, request, vector, cancellationToken));
        return Task.WhenAll(calls);
    }

    private static async Task FadedAsync(PackStore store, RecallOptions shipped, RecordRequest request, float[] vector, CancellationToken cancellationToken)
    {
        using var scope = store.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>()
            .SearchDecayedFactsAsync(vector, 10, shipped.MinSimilarityScore, MemoryScope.For(store.Owner(request.Owner), includeShared: false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The main recall for a set of doors: each searching door at the shipped caps (or wide open), a closed door's cap 0 (the
    /// library then skips its search); the temporal door before the search reads the facts as currently valid. The profile
    /// and the last messages (the working door) load on every recall.
    /// </summary>
    internal static RecallOptions Options(RecallOptions shipped, IReadOnlySet<Door> doors, bool wide)
    {
        var b = wide ? Recorder.WideOpen(shipped) : shipped;
        var facts = doors.Contains(Door.Semantic) || doors.Contains(Door.Derived) || doors.Contains(Door.Temporal);
        var graph = doors.Contains(Door.EntityGraph);
        return b with
        {
            MaxFacts = facts ? b.MaxFacts : 0,
            MaxEntities = graph ? b.MaxEntities : 0,
            MaxRelationships = graph ? b.MaxRelationships : 0,
            MaxPreferences = doors.Contains(Door.Preference) ? b.MaxPreferences : 0,
            MaxRelevantMessages = doors.Contains(Door.Episodic) ? b.MaxRelevantMessages : 0,
            MaxTraces = doors.Contains(Door.Procedural) || doors.Contains(Door.Reasoning) ? b.MaxTraces : 0,
            ValidTime = !wide && doors.Contains(Door.Temporal) ? ValidTimeMode.Current : b.ValidTime,
            IncludeDiagnostics = false,
        };
    }

    /// <summary>One door, searched wide (its reading, for a reading door), and nothing else.</summary>
    private sealed class OneDoor(Door door) : IContestant
    {
        public string Family => "door";

        public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?> { ["door"] = Doors.Name(door) };

        public Decision Decide(MatrixItem item, TurnContext context) =>
            new(new HashSet<string>(), new HashSet<Door> { door }, 1) { Wide = Doors.Searching.Contains(door), SearchedDoors = new HashSet<Door> { door } };
    }

    private static double P(List<double> values, int percentile)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1)], 1);
    }
}
