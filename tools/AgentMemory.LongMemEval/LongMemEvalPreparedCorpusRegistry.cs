using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The catalog of frozen corpora on this machine: what each one is, when it was built, and how.
/// </summary>
/// <remarks>
/// <para>
/// A cold build costs ~684 extraction calls and 7–9 hours, so frozen corpora are kept and reused for
/// weeks. Until now the only record of what any of them contained was the volume name, a timestamp in
/// it, and whatever a human remembered — so "is this one still usable?" was answered from notes lying
/// around, which is exactly how a run ends up measuring the wrong corpus.
/// </para>
/// <para>
/// <b>The registry is a convenience, never the guarantee.</b> The authority is the manifest sealed
/// <i>inside</i> each corpus, which travels with the data and cannot drift from it; this file can be
/// deleted, hand-edited or lost without making a single reuse unsafe, because
/// <see cref="LongMemEvalPreparedCorpusDrift"/> reads the sealed manifest rather than this. What the
/// registry buys is the ability to answer "what do I have, and which one should I reuse?" without
/// starting a container per candidate.
/// </para>
/// <para>
/// Machine-local by design, and it lives under <c>artifacts/</c> — the corpora it describes are local
/// Docker volumes, so a shared copy would list things that do not exist wherever it was read.
/// </para>
/// </remarks>
internal static class LongMemEvalPreparedCorpusRegistry
{
    internal const string DefaultPath = "artifacts/evaluation/prepared-corpora.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Appends (or replaces) the entry for a corpus that has just been sealed.</summary>
    /// <remarks>
    /// Best-effort on purpose: a registry write must never fail a build that already cost nine hours
    /// and is already sealed and usable. The corpus is complete whether or not it gets catalogued.
    /// </remarks>
    internal static void Record(
        string volumeName,
        LongMemEvalPreparationManifest manifest,
        TextWriter diagnostics,
        string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var file = path ?? DefaultPath;
        try
        {
            var entries = Read(file).Where(e => !string.Equals(e.Volume, volumeName, StringComparison.Ordinal))
                .ToList();
            entries.Add(new PreparedCorpusEntry
            {
                Volume = volumeName,
                PreparationId = manifest.PreparationId,
                PreparedAtUtc = manifest.PreparedAtUtc,
                Description = manifest.Description,
                Questions = manifest.Questions.Count,
                Seed = manifest.QuestionSeed,
                MemoryTypes = manifest.MemoryTypes is { Count: > 0 } types ? [.. types] : ["all"],
                ManifestSchemaVersion = manifest.SchemaVersion,
                Fingerprint = manifest.Fingerprint,
                DatasetSha256 = manifest.DatasetSha256,
                ExtractionModel = manifest.ExtractionModelId,
                EmbeddingModel = manifest.EmbeddingModelId,
                EmbeddingDimensions = manifest.EmbeddingDimensions,
                AssistantContent = manifest.AssistantContent,
                ExtractionProvenance = manifest.ExtractionProvenance,
                ExtractionVocabularySha256 = manifest.ExtractionVocabularySha256,
                AgentEvalRevision = manifest.AgentEvalRevision,
            });

            var directory = Path.GetDirectoryName(Path.GetFullPath(file));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(file, JsonSerializer.Serialize(entries, Json));
            diagnostics.WriteLine($"longmemeval: catalogued prepared corpus '{volumeName}' in {file}.");
        }
        catch (Exception ex)
        {
            diagnostics.WriteLine(
                $"longmemeval: could not update the prepared-corpus registry ({ex.Message}). The corpus "
                + "is sealed and reusable regardless -- the registry is a convenience, and the manifest "
                + "inside the volume is what reuse actually checks.");
        }
    }

    /// <summary>Every catalogued corpus, newest first. Empty when the registry does not exist yet.</summary>
    internal static IReadOnlyList<PreparedCorpusEntry> Read(string? path = null)
    {
        var file = path ?? DefaultPath;
        if (!File.Exists(file)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PreparedCorpusEntry>>(File.ReadAllText(file), Json)
                ?.OrderByDescending(e => e.PreparedAtUtc, StringComparer.Ordinal).ToList() ?? [];
        }
        catch (JsonException)
        {
            // A corrupt registry is not a reason to fail; it is a reason to say so and carry on with
            // nothing catalogued, because the sealed manifests remain the authority either way.
            return [];
        }
    }

    /// <summary>A human-readable listing, for the <c>--list-prepared-corpora</c> verb.</summary>
    /// <remarks>
    /// Age is shown because staleness is usually a judgement rather than a hard mismatch: a corpus can
    /// match on every ingestion field and still predate a change to the extraction prompt that nobody
    /// fingerprinted. Naming the date lets the operator apply that judgement; naming only "OK" would
    /// invite them not to.
    /// </remarks>
    internal static string Describe(IReadOnlyList<PreparedCorpusEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return "No prepared corpora catalogued. A cold build costs ~684 extraction calls and 7-9 "
                + "hours for 50 questions, so build one with --prepared-pair --retain-prepared-volumes "
                + "--description \"...\" and it will be listed here.";
        }

        var lines = entries.Select(entry =>
        {
            var age = DateTimeOffset.TryParse(
                entry.PreparedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                ? $"{(now - when).TotalDays:0} day(s) old"
                : "age unknown";
            var types = entry.MemoryTypes is { Count: > 0 } t ? string.Join("+", t) : "all";
            var schema = entry.ManifestSchemaVersion < LongMemEvalPreparationManifest.CurrentSchemaVersion
                ? $" [manifest schema {entry.ManifestSchemaVersion} < {LongMemEvalPreparationManifest.CurrentSchemaVersion}"
                  + " - some ingestion settings were not recorded, so reuse will report them as drift]"
                : string.Empty;
            return $"  {entry.Volume}\n"
                + $"    {entry.Questions}q seed={entry.Seed} types={types} | {age} ({entry.PreparedAtUtc})\n"
                + $"    extraction={entry.ExtractionModel} embedding={entry.EmbeddingModel}/{entry.EmbeddingDimensions}"
                + $" assistantContent={entry.AssistantContent} provenance={entry.ExtractionProvenance}\n"
                + $"    {(string.IsNullOrWhiteSpace(entry.Description) ? "(no description)" : entry.Description)}{schema}";
        });

        return $"{entries.Count} prepared corpus/corpora:\n" + string.Join("\n", lines);
    }
}

/// <summary>One catalogued corpus.</summary>
/// <remarks>
/// Mirrors the sealed manifest's ingestion identity rather than referencing it, so the listing can be
/// read without attaching to a container — which is the whole point of having a catalog.
/// </remarks>
internal sealed record PreparedCorpusEntry
{
    public string Volume { get; init; } = "";
    public string PreparationId { get; init; } = "";
    public string PreparedAtUtc { get; init; } = "";
    public string Description { get; init; } = "";
    public int Questions { get; init; }
    public int Seed { get; init; }
    public IReadOnlyList<string> MemoryTypes { get; init; } = [];
    public int ManifestSchemaVersion { get; init; }
    public string Fingerprint { get; init; } = "";
    public string DatasetSha256 { get; init; } = "";
    public string ExtractionModel { get; init; } = "";
    public string EmbeddingModel { get; init; } = "";
    public int EmbeddingDimensions { get; init; }
    public string AssistantContent { get; init; } = "";
    public string ExtractionProvenance { get; init; } = "";
    public string ExtractionVocabularySha256 { get; init; } = "";
    public string AgentEvalRevision { get; init; } = "";
}
