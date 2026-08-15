using System.Globalization;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Whether a frozen corpus was built the way the current run intends to evaluate it.
/// </summary>
/// <remarks>
/// <para>
/// A cold build costs ~684 extraction calls and 7–9 hours, so every real measurement reuses a frozen
/// one. Reuse read the sealed manifest, printed its fingerprint, and proceeded — and
/// <c>VerifyIntegrity</c> only checks the manifest against <i>itself</i>, that its recorded fingerprint
/// matches its recorded contents. Nothing compared it to <b>this</b> run.
/// </para>
/// <para>
/// So a corpus ingested with <c>AssistantContent=Utterance</c> under one model could be evaluated by a
/// run configured for <c>Ignore</c> under another, and the report's own fingerprint block would
/// describe the run's configuration while every fact in the graph came from the other one. The number
/// would be wrong, internally consistent, and reproducible — the worst combination. That is the
/// failure this exists to make impossible.
/// </para>
/// <para>
/// <b>What counts as drift is deliberately narrow: only what changed the stored graph.</b> The answer
/// model, the judge, the recall budget and the evidence mode all matter to a result but are applied at
/// evaluation time, so a frozen corpus is legitimately reusable across them — that reusability is the
/// entire point. Flagging them would make every reuse "stale" and train the operator to pass the
/// override, which is worse than having no check.
/// </para>
/// </remarks>
internal static class LongMemEvalPreparedCorpusDrift
{
    /// <summary>One field whose value differs between the frozen corpus and this run.</summary>
    internal readonly record struct Difference(string Field, string Prepared, string Current)
    {
        public override string ToString() =>
            $"{Field}: corpus={Format(Prepared)} run={Format(Current)}";

        private static string Format(string value) =>
            string.IsNullOrEmpty(value) ? "(unrecorded)" : value;
    }

    /// <summary>
    /// The ingestion-affecting fields on which <paramref name="prepared"/> and the current run differ.
    /// </summary>
    /// <param name="prepared">The manifest sealed inside the frozen corpus.</param>
    /// <param name="current">What this run would have built, had it built one.</param>
    /// <remarks>
    /// A field the corpus never recorded — anything sealed under schema 5 or earlier — is reported as
    /// a difference rather than skipped. "We do not know what this corpus was built with" is not
    /// evidence that it matches; treating unknown as equal is how a check stops being able to fail.
    /// </remarks>
    internal static IReadOnlyList<Difference> Compare(
        LongMemEvalPreparationManifest prepared, PreparedCorpusIdentity current)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(current);

        var differences = new List<Difference>();

        // Schema 5 and earlier did not carry the ingestion-identity fields at all, so deserialising
        // one fills them from the record's DEFAULTS -- "Ignore", "Batch", false. Those are plausible
        // values, and comparing against them would report a corpus built with Utterance as MATCHING a
        // run configured for Ignore: unknown silently reading as agreement, which is the one thing
        // this check exists to prevent. Older manifests therefore report every such field as
        // unrecorded, whatever the default happened to be.
        var recordsIngestionIdentity = prepared.SchemaVersion >= 6;
        string Recorded(string value) => recordsIngestionIdentity ? value : string.Empty;

        void Check(string field, string preparedValue, string currentValue)
        {
            if (!string.Equals(preparedValue, currentValue, StringComparison.Ordinal))
                differences.Add(new Difference(field, preparedValue, currentValue));
        }

        Check("datasetSha256", prepared.DatasetSha256, current.DatasetSha256);
        Check("extractionModel", prepared.ExtractionModelId, current.ExtractionModelId);
        Check("embeddingModel", prepared.EmbeddingModelId, current.EmbeddingModelId);
        Check("embeddingDimensions",
            prepared.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture),
            current.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture));
        Check("assistantContent", Recorded(prepared.AssistantContent), current.AssistantContent);
        Check("extractionProvenance", Recorded(prepared.ExtractionProvenance), current.ExtractionProvenance);
        Check("usePredicateVocabulary",
            Recorded(prepared.UsePredicateVocabulary.ToString()), current.UsePredicateVocabulary.ToString());
        // 30.1. Deliberately NOT routed through Recorded(). Every other ingestion field could hold a
        // plausible-but-wrong default on an older manifest, which is why "unrecorded" is treated as
        // drift there. The seed cannot: LlmExtractionOptions.Seed had no writer in this harness before
        // schema 7, so a corpus sealed under 6 or earlier was PROVABLY built unseeded. Reporting it as
        // unrecorded would drift every frozen corpus against every unseeded run and train the operator
        // to pass --allow-stale-prepared, which the header above names as worse than no check at all.
        Check("extractionSeed",
            FormatSeed(prepared.ExtractionSeed), FormatSeed(current.ExtractionSeed));
        Check("extractionVocabularySha256",
            prepared.ExtractionVocabularySha256, current.ExtractionVocabularySha256);
        Check("queryRelationLexiconSha256",
            prepared.QueryRelationLexiconSha256, current.QueryRelationLexiconSha256);
        Check("questionSeed",
            Recorded(prepared.QuestionSeed.ToString(CultureInfo.InvariantCulture)),
            current.QuestionSeed.ToString(CultureInfo.InvariantCulture));
        Check("questionCount",
            prepared.Questions.Count.ToString(CultureInfo.InvariantCulture),
            current.QuestionCount.ToString(CultureInfo.InvariantCulture));
        Check("abstention",
            Recorded(prepared.AbstentionPolicy), current.AbstentionPolicy);
        Check("memoryTypes",
            Recorded(string.Join(",", (prepared.MemoryTypes ?? []).OrderBy(t => t, StringComparer.Ordinal))),
            string.Join(",", current.MemoryTypes.OrderBy(t => t, StringComparer.Ordinal)));

        return differences;
    }

    /// <summary>
    /// Renders a seed for comparison. "none" rather than empty, so the message reads
    /// <c>corpus=none run=20260815</c> instead of claiming the corpus never recorded the field.
    /// </summary>
    private static string FormatSeed(int? seed) =>
        seed?.ToString(CultureInfo.InvariantCulture) ?? "none";

    /// <summary>
    /// The operator-facing explanation of a refusal, naming every drifted field.
    /// </summary>
    /// <remarks>
    /// Names the override and what it means, because there is a legitimate use for it — deliberately
    /// evaluating an older corpus with a newer answer model, say — and an operator who cannot find the
    /// escape hatch reaches for a cold build instead, which costs nine hours to avoid a warning.
    /// </remarks>
    internal static string Explain(
        string volumeName, string preparedAtUtc, IReadOnlyList<Difference> differences)
    {
        var age = string.IsNullOrEmpty(preparedAtUtc) ? "unknown date" : $"prepared {preparedAtUtc}";
        var lines = string.Join(Environment.NewLine, differences.Select(d => $"  - {d}"));
        return
            $"Prepared corpus '{volumeName}' ({age}) was not built the way this run would build it. "
            + $"{differences.Count} ingestion-affecting field(s) differ:{Environment.NewLine}{lines}"
            + Environment.NewLine
            + "Evaluating it anyway would report THIS run's configuration over a graph built with "
            + "another one — a result that is internally consistent, reproducible, and wrong. Either "
            + "prepare a fresh corpus, or pass --allow-stale-prepared if the difference is one you "
            + "intend (evaluating an older corpus with a newer answer model, for example); the "
            + "override records the drift in the report rather than hiding it.";
    }
}

/// <summary>
/// What a run would build: the ingestion-affecting half of its configuration.
/// </summary>
/// <remarks>
/// Separate from <see cref="LongMemEvalPreparationManifest"/> on purpose. The manifest describes a
/// corpus that exists, with per-question hashes and call counts; this describes an intent, and can be
/// constructed before anything is built or adopted — which is what lets the check run before the first
/// provider call rather than after.
/// </remarks>
internal sealed record PreparedCorpusIdentity
{
    internal required string DatasetSha256 { get; init; }
    internal required string ExtractionModelId { get; init; }
    internal required string EmbeddingModelId { get; init; }
    internal required int EmbeddingDimensions { get; init; }
    internal required string AssistantContent { get; init; }
    internal string ExtractionProvenance { get; init; } = "Batch";
    internal required bool UsePredicateVocabulary { get; init; }

    /// <summary>The extraction sampling seed this run would build with; null for none.</summary>
    internal int? ExtractionSeed { get; init; }
    internal required string ExtractionVocabularySha256 { get; init; }
    internal required string QueryRelationLexiconSha256 { get; init; }
    internal required int QuestionSeed { get; init; }
    internal required int QuestionCount { get; init; }
    internal IReadOnlyList<string> MemoryTypes { get; init; } = [];

    /// <summary>
    /// The abstention sampling policy, which decides whether unanswerable questions are in the sample.
    /// </summary>
    /// <remarks>
    /// Ingestion-affecting, not merely evaluation-affecting: an abstention question still carries a
    /// conversation history, so including one changes which histories were ingested. A corpus built
    /// without them cannot answer a run that expects them.
    /// </remarks>
    internal string AbstentionPolicy { get; init; } = "AsSampled";
}
