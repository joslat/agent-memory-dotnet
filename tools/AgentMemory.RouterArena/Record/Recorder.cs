using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using AgentMemory.RouterArena.Data;
using AgentMemory.Validation;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// <c>record</c>: what recall returns for every matrix turn, kind by kind, with each item's score and owner, so every
/// contestant is scored on the recording offline, as often as needed. The world (a validation pack) goes into a Neo4j of
/// its own (Testcontainers, removed after) with real embeddings from a local Ollama: no store of anyone's is touched and no
/// model is called. Per turn: the recall as shipped (the old method, fan-out on as the conversational preset ships it,
/// with its witness), the same with fan-out off (the old method without the adaptive router), wide open (every kind up to
/// 50, no floor: what a gate chooses from), wide open on the turn rewritten with the one before, and the shipped recall at
/// other floors and caps (the old method re-tuned, for real).
/// </summary>
public sealed class Recorder(TextWriter output)
{
    /// <summary>The Neo4j image of every throwaway store this repository's tools run (the CLI's <c>perf</c> uses the same).</summary>
    public const string Image = "neo4j:5.26";

    /// <summary>The wide recall's cap per kind: the world holds fewer memories than this of any kind.</summary>
    public const int Wide = 50;

    public async Task<int> RunAsync(RecordRequest request, CancellationToken cancellationToken = default)
    {
        ValidationPack world;
        IReadOnlyList<MatrixItem> turns;
        try
        {
            world = ValidationPackReader.ReadFile(request.WorldPath);
            turns = Matrix.Read(request.ItemsPath).Items;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            output.WriteLine($"error: record: {ex.Message}");
            return 1;
        }
        var problems = ValidationPackReader.Check(world).ToList();
        if (problems.Count > 0)
        {
            output.WriteLine($"error: record: the world pack does not check: {string.Join("; ", problems)}");
            return 1;
        }

        await using var matrix = await MatrixStore.StartAsync(world, request, output, "record", cancellationToken).ConfigureAwait(false);
        if (matrix is null) return 1;
        var (store, embeddings, dimensions) = (matrix.Store, matrix.Embeddings, matrix.Dimensions);
        output.WriteLine($"record: {turns.Count} turns; recalling every turn…");

        var shipped = store.Options.Recall with { IncludeDiagnostics = true };
        var fanOutShipped = store.Options.FanOut.Enabled;
        var records = new List<TurnRecord>();
        foreach (var turn in turns)
        {
            Task<MemoryContext> Recall(string query, RecallOptions options) =>
                store.RecallAsync(request.Owner, request.Session, query, request.AskedAt, _ => options, cancellationToken: cancellationToken);

            var today = await Recall(turn.Text, shipped).ConfigureAwait(false);
            // The same recall without the adaptive router (fan-out reads its switch per recall from the shared options).
            store.Options.FanOut.Enabled = false;
            MemoryContext noFanOut;
            try
            {
                noFanOut = await Recall(turn.Text, shipped).ConfigureAwait(false);
            }
            finally
            {
                store.Options.FanOut.Enabled = fanOutShipped;
            }
            var wide = await Recall(turn.Text, WideOpen(shipped)).ConfigureAwait(false);
            var priorUser = turn.Prior.LastOrDefault(p => p.Role == "user")?.Text;
            var withPrior = priorUser is null ? null : await Recall($"{priorUser} {turn.Text}", WideOpen(shipped)).ConfigureAwait(false);
            Dictionary<string, Section>? variants = null;
            foreach (var (floor, scale) in request.Variants)
            {
                int Scaled(int cap) => Math.Max(1, (int)Math.Round(cap * scale));
                var variant = await Recall(turn.Text, shipped with
                {
                    MinSimilarityScore = floor,
                    MaxFacts = Scaled(shipped.MaxFacts), MaxEntities = Scaled(shipped.MaxEntities),
                    MaxRelationships = Scaled(shipped.MaxRelationships), MaxPreferences = Scaled(shipped.MaxPreferences),
                    MaxRelevantMessages = Scaled(shipped.MaxRelevantMessages),
                }).ConfigureAwait(false);
                (variants ??= [])[string.Create(CultureInfo.InvariantCulture, $"{floor:0.00}x{scale:0.#}")] = Section.Of(variant);
            }
            // The doors that are ways of reading the store (40.72).
            var doors = new Dictionary<string, Section>(StringComparer.Ordinal)
            {
                ["temporal"] = Section.Of(await Recall(turn.Text, shipped with { ValidTime = ValidTimeMode.Current }).ConfigureAwait(false)),
                // The same wide open: what a gate after the search judges when the temporal door is open.
                ["temporal:wide"] = Section.Of(await Recall(turn.Text, WideOpen(shipped) with { ValidTime = ValidTimeMode.Current }).ConfigureAwait(false)),
                ["prospective"] = Section.Of(await Recall(turn.Text, shipped with { ValidTime = ValidTimeMode.Current, ProspectiveFiring = true })
                    .ConfigureAwait(false)),
                ["forgetting"] = Section.Of(await Recall(turn.Text, shipped with { LegibleForgetting = true }).ConfigureAwait(false)),
            };
            // The forgetting switch as a router would flip it: the probe for faded memories, asked directly. The library
            // runs it only when the live facts come back empty (a thin recall), so a router cannot ask for it today.
            var vector = (await embeddings.GenerateAsync([turn.Text], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.ToArray();
            using (var probe = store.Provider.CreateScope())
            {
                var decayed = await probe.ServiceProvider.GetRequiredService<ILongTermMemoryService>()
                    .SearchDecayedFactsAsync(vector, 10, shipped.MinSimilarityScore, MemoryScope.For(store.Owner(request.Owner), includeShared: false), cancellationToken)
                    .ConfigureAwait(false);
                doors["faded"] = new Section([.. decayed.Select((f, i) => new Hit($"{f.Subject} | {f.Predicate} | {f.Object}", null, i + 1, f.OwnerId, "faded"))],
                    [], [], [], [], [], null);
            }
            foreach (var then in request.AsOfDates)
                doors[$"asOf:{then:yyyy-MM-dd}"] = Section.Of(await store.RecallAsync(request.Owner, request.Session, turn.Text, request.AskedAt,
                    _ => shipped, new PackAsOf { Valid = then, System = request.AskedAt }, cancellationToken).ConfigureAwait(false));
            records.Add(new TurnRecord(turn.Id, turn.Text, Section.Of(today), Section.Of(wide),
                withPrior is null ? null : Section.Of(withPrior), variants, Section.Of(noFanOut), FanOutWitness.Of(today.FanOutReport), doors));
            if (records.Count % 25 == 0) output.WriteLine($"  {records.Count}/{turns.Count}");
        }

        var recording = new Recording(
            Format: "routing-recording/2",
            World: new Source(Path.GetFullPath(request.WorldPath), Sha256(request.WorldPath)),
            Items: new Source(Path.GetFullPath(request.ItemsPath), Sha256(request.ItemsPath)),
            Embeddings: $"{request.Model} ({dimensions}) via Ollama",
            AskedAt: request.AskedAt,
            Owner: request.Owner,
            Session: request.Session,
            Today: new Caps(shipped.MaxFacts, shipped.MaxEntities, shipped.MaxRelationships, shipped.MaxPreferences,
                shipped.MaxRelevantMessages, shipped.MaxRecentMessages, shipped.MinSimilarityScore) { Traces = shipped.MaxTraces },
            Records: records)
        { FanOutShipped = fanOutShipped };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutPath))!);
        await File.WriteAllTextAsync(request.OutPath, JsonSerializer.Serialize(recording, Recording.Json), cancellationToken).ConfigureAwait(false);
        output.WriteLine($"record: {records.Count} turns recorded to {request.OutPath}");
        return 0;
    }

    internal static RecallOptions WideOpen(RecallOptions recall) => recall with
    {
        MaxFacts = Wide, MaxEntities = Wide, MaxRelationships = Wide, MaxPreferences = Wide,
        MaxRelevantMessages = Wide, MaxRecentMessages = 10, MaxTraces = 10, MinSimilarityScore = 0, IncludeDiagnostics = true,
    };

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

/// <summary>What to record: the world, the matrix, where to write, and the old method's other settings ("0.65:2").</summary>
public sealed record RecordRequest(
    string WorldPath, string ItemsPath, string OutPath, IReadOnlyList<(double Floor, double Scale)> Variants,
    string Owner = "marta", string Session = "marta-now", string Ollama = "http://127.0.0.1:11434", string Model = "bge-m3")
{
    public DateTimeOffset AskedAt { get; init; } = DateTimeOffset.Parse("2026-10-03T10:00:00Z", CultureInfo.InvariantCulture);

    /// <summary>When the decay pass runs (the forgetting switch): only memories older than about a hundred days then fade.</summary>
    public DateTimeOffset DecayAt { get; init; } = DateTimeOffset.Parse("2023-08-29T00:00:00Z", CultureInfo.InvariantCulture);

    /// <summary>The bi-temporal door's anchors: what was true a year ago, and before the move to Lyon.</summary>
    public IReadOnlyList<DateTimeOffset> AsOfDates { get; init; } =
        [DateTimeOffset.Parse("2025-10-03T10:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2020-06-01T10:00:00Z", CultureInfo.InvariantCulture)];

    public static IReadOnlyList<(double Floor, double Scale)> ParseVariants(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.Split(':'))
            .Select(v => (double.Parse(v[0], CultureInfo.InvariantCulture), v.Length > 1 ? double.Parse(v[1], CultureInfo.InvariantCulture) : 1.0))];
}
