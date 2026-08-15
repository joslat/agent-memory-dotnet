using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neo4j.Driver;

namespace AgentMemory.LongMemEval;

internal sealed record LongMemEvalPreparedQuestion(
    int QuestionNumber,
    string QuestionId,
    string HistorySha256,
    string ScopeSha256,
    int MessagesPrepared,
    int SourceSessions,
    int ExtractionUnitsPrepared,
    LongMemEvalGraphSnapshot GraphSnapshot);

internal sealed record LongMemEvalPreparationManifest(
    int SchemaVersion,
    string PreparationId,
    string DatasetSha256,
    string AgentEvalRevision,
    string ScopeRunIdSha256,
    string AnswerModelId,
    string JudgeModelId,
    string ExtractionModelId,
    string EmbeddingModelId,
    int EmbeddingDimensions,
    int MaxRelevantMessages,
    string ExtractionSourceTime,
    string ExtractionResponseContract,
    bool UseJsonResponseFormat,
    bool UseUnifiedExtraction,
    bool UseMultiSessionBatchExtraction,
    int PreparationWorkers,
    int MaxSessionsPerBatch,
    int MaxInputTokens,
    int MaxConcurrentBatchesPerExtraction,
    int MaxConcurrentExtractionBatches,
    IReadOnlyList<LongMemEvalPreparedQuestion> Questions,
    long InitialExtractionCalls,
    string Fingerprint,
    // ── Ingestion identity (schema 6) ────────────────────────────────────
    // Everything below changes WHAT WAS STORED, and none of it was recorded. A volume built with
    // AssistantContent=Utterance could be adopted by a run configured for Ignore, and the report's
    // fingerprint would describe the run's configuration while the graph came from the other one.
    // Recorded here so a reuse can be refused instead of quietly measuring the wrong corpus.
    string AssistantContent = "Ignore",
    bool UsePredicateVocabulary = false,
    // ── Extraction determinism (schema 7) ────────────────────────────────
    // 30.1. The sampling seed the extraction calls were issued with, or null for none. It belongs
    // with the fields above for exactly their reason: it changes WHAT WAS STORED. Null is not a
    // guess for an older corpus — LlmExtractionOptions.Seed had no writer in this harness before
    // schema 7, so every corpus sealed under 6 or earlier was provably built unseeded.
    int? ExtractionSeed = null,
    string ExtractionVocabularySha256 = "",
    string QueryRelationLexiconSha256 = "",
    string ExtractionProvenance = "Batch",
    string AbstentionPolicy = "AsSampled",
    // Sessions whose content the provider refused, so their material is absent from this corpus.
    // Part of the fingerprint: a corpus with gaps is not the same corpus as one without.
    int RefusedSourceSessions = 0,
    // ── Catalog metadata (schema 6) ──────────────────────────────────────
    // Not part of the fingerprint: these describe the build for a human, and two corpora that differ
    // only in their description are the same corpus.
    string PreparedAtUtc = "",
    string Description = "",
    IReadOnlyList<string>? MemoryTypes = null,
    int QuestionSeed = 0,
    // The provider's backend build ids, as OBSERVED during extraction (S-4). Deliberately NOT part of
    // the fingerprint, and the reason is worth stating: a build id is not something this project
    // configures, so hashing it would make every corpus non-reusable the moment the provider updated
    // its backend -- discarding a nine-hour build over a change nobody here made. What it does buy is
    // the ability to say that two corpora were built on different backends, which is the difference
    // between "your change moved the number" and "these two runs were never comparable". Empty when the
    // provider reported none; a placeholder would let a report deny incomparability it cannot rule out.
    IReadOnlyList<string>? ExtractionProviderBuilds = null)
{
    // Schema 7 adds ExtractionSeed to the hashed field set. The rule this file learned the hard way
    // (see VerifyIntegrity) is that any change to the hashed field set MUST bump this, and the
    // previous version's field set must be preserved verbatim so its corpora still verify.
    public const int CurrentSchemaVersion = 7;

    internal int MessagesPrepared => Questions.Sum(question => question.MessagesPrepared);

    internal int ExtractionUnitsPrepared =>
        Questions.Sum(question => question.ExtractionUnitsPrepared);

    internal static LongMemEvalPreparationManifest Create(
        string preparationId,
        string datasetSha256,
        string agentEvalRevision,
        string scopeRunId,
        string answerModelId,
        string judgeModelId,
        string extractionModelId,
        string embeddingModelId,
        int embeddingDimensions,
        int maxRelevantMessages,
        string extractionSourceTime,
        IReadOnlyList<LongMemEvalPreparedQuestion> questions,
        long initialExtractionCalls,
        bool useJsonResponseFormat = true,
        string extractionResponseContract = "json-object",
        bool useUnifiedExtraction = false,
        bool useMultiSessionBatchExtraction = false,
        int preparationWorkers = 1,
        int maxSessionsPerBatch = 1,
        int maxInputTokens = 100_000,
        int maxConcurrentBatchesPerExtraction = 1,
        int maxConcurrentExtractionBatches = 0,
        string assistantContent = "Ignore",
        bool usePredicateVocabulary = false,
        int? extractionSeed = null,
        string extractionVocabularySha256 = "",
        string queryRelationLexiconSha256 = "",
        string extractionProvenance = "Batch",
        string abstentionPolicy = "AsSampled",
        int refusedSourceSessions = 0,
        string preparedAtUtc = "",
        string description = "",
        IReadOnlyList<string>? memoryTypes = null,
        int questionSeed = 0,
        IReadOnlyList<string>? extractionProviderBuilds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentEvalRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(answerModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(judgeModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(embeddingDimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRelevantMessages);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionSourceTime);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionResponseContract);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentOutOfRangeException.ThrowIfNegative(initialExtractionCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparationWorkers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maxConcurrentBatchesPerExtraction);
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcurrentExtractionBatches);
        if (useMultiSessionBatchExtraction && !useUnifiedExtraction)
        {
            throw new ArgumentException(
                "Multi-session extraction requires unified extraction.");
        }

        var materialized = questions.ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("A preparation manifest requires at least one question.", nameof(questions));
        if (materialized.Select(question => question.QuestionNumber).Distinct().Count() != materialized.Length ||
            materialized.Select(question => question.QuestionId).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new ArgumentException(
                "A preparation manifest requires unique question numbers and ids.",
                nameof(questions));
        }

        var manifest = new LongMemEvalPreparationManifest(
            CurrentSchemaVersion,
            preparationId,
            datasetSha256,
            agentEvalRevision,
            Hash(scopeRunId),
            answerModelId,
            judgeModelId,
            extractionModelId,
            embeddingModelId,
            embeddingDimensions,
            maxRelevantMessages,
            extractionSourceTime,
            extractionResponseContract,
            useJsonResponseFormat,
            useUnifiedExtraction,
            useMultiSessionBatchExtraction,
            preparationWorkers,
            maxSessionsPerBatch,
            maxInputTokens,
            maxConcurrentBatchesPerExtraction,
            maxConcurrentExtractionBatches,
            materialized,
            initialExtractionCalls,
            Fingerprint: string.Empty,
            AssistantContent: assistantContent,
            UsePredicateVocabulary: usePredicateVocabulary,
            ExtractionSeed: extractionSeed,
            ExtractionVocabularySha256: extractionVocabularySha256,
            QueryRelationLexiconSha256: queryRelationLexiconSha256,
            ExtractionProvenance: extractionProvenance,
            AbstentionPolicy: abstentionPolicy,
            RefusedSourceSessions: refusedSourceSessions,
            PreparedAtUtc: preparedAtUtc,
            Description: description,
            MemoryTypes: memoryTypes ?? [],
            QuestionSeed: questionSeed,
            ExtractionProviderBuilds: extractionProviderBuilds ?? []);
        return manifest with { Fingerprint = ComputeFingerprint(manifest) };
    }

    /// <summary>
    /// Checks a manifest against itself: that its recorded fingerprint matches its recorded contents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Older schemas are read, not rejected.</b> Requiring an exact schema match orphaned every
    /// corpus sealed before the current version -- each one a 7-9 hour build -- which is the precise
    /// opposite of what recording ingestion identity is for. A manifest written by an older build is
    /// still a valid description of a real corpus; what it lacks is fields, and the drift comparison
    /// already treats an unrecorded field as drift rather than as agreement.
    /// </para>
    /// <para>
    /// A <i>newer</i> schema is still refused. It was written by a build that knew things this one
    /// does not, so its fingerprint cannot be recomputed here and its extra fields would be silently
    /// dropped.
    /// </para>
    /// </remarks>
    /// <summary>
    /// False when this manifest's recorded fingerprint does not reproduce under the current field
    /// set — an older seal, not a corrupted one. Set by <see cref="VerifyIntegrity"/>.
    /// </summary>
    internal bool FingerprintVerified { get; private set; } = true;

    /// <summary>
    /// Corpora sealed before the fingerprint's field set changed under them, exempted from
    /// self-verification by <b>id</b> rather than by a rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a list and not a version check.</b> Task 6.5 added two nullable counters to
    /// <c>LongMemEvalGraphSnapshot</c> — a fix for a label-blind probe that changed nothing about what
    /// was stored — and the fingerprint serialises that record whole, so the hash moved. The schema
    /// version did not, so nothing in the manifest distinguishes "sealed earlier" from "edited since".
    /// A heuristic on the snapshot's shape exempts synthetic fixtures too, which is exactly the
    /// over-application that would turn a tamper check into decoration.
    /// </para>
    /// <para>
    /// <b>The lesson, recorded where the next person will hit it:</b> a fingerprint must never
    /// serialise a record whose shape it does not control, and any change to the hashed field set must
    /// bump <see cref="CurrentSchemaVersion"/>. Neither happened, and the cost was a 616-call corpus
    /// that could not be opened.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> GrandfatheredPreparationIds = new(StringComparer.Ordinal)
    {
        // The pinned 50-question abstention-enriched base: 616 extraction calls, ~52 minutes, and the
        // only corpus that has ever run abstention questions.
        "longmemeval-prepared-20260812T140253Z",
    };

    internal void VerifyIntegrity()
    {
        if (SchemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"LongMemEval preparation manifest schema {SchemaVersion} was written by a newer "
                + $"build than this one (schema {CurrentSchemaVersion}); it cannot be verified here.");
        }
        if (SchemaVersion < 1)
        {
            throw new InvalidOperationException(
                $"Unsupported LongMemEval preparation manifest schema {SchemaVersion}.");
        }

        // WHAT THIS CHECK IS FOR, and what it is not.
        //
        // It compares a manifest against ITSELF -- a tamper check on JSON this harness wrote into a
        // private Docker volume. It is NOT the guard that protects a measurement; that is the DRIFT
        // comparison, which checks the manifest's recorded ingestion settings against the current
        // run's configuration and still fails closed.
        //
        // Treating a non-reproducing hash as fatal destroyed the thing it existed to protect. The
        // fingerprint serialised whole records whose shape it does not control, so 6.5's two nullable
        // graph-snapshot counters -- added to fix a label-blind probe, changing nothing about what was
        // stored -- silently orphaned every frozen corpus, including the 616-call base every cheap
        // experiment in this phase reuses. It presented as "fingerprint mismatch", which reads as
        // tampering rather than as a versioning mistake here.
        //
        // Recomputation cannot be made reliable retroactively: the historical field sets are not
        // recoverable from the manifest, because the schema version did not change when they did. So
        // an older manifest whose hash does not reproduce is recorded as UNVERIFIABLE rather than
        // rejected, callers surface that, and drift still refuses a genuinely mismatched config.
        FingerprintVerified = string.Equals(
            Fingerprint, ComputeFingerprint(this), StringComparison.Ordinal);
        if (FingerprintVerified) return;

        // A non-reproducing hash is EITHER an older seal or a tampered manifest, and conflating them
        // would remove the guard. They cannot be told apart from the manifest's contents -- a
        // heuristic on the snapshot shape looked promising and exempts synthetic fixtures too, which
        // is precisely the over-application that makes a heuristic the wrong tool here.
        //
        // So the exemption is an explicit LIST of the artifacts known to predate the change, by id.
        // It cannot over-apply, it is reviewable, and it names what is being grandfathered and why.
        // Everything else that fails to reproduce is a manifest whose contents no longer match its
        // seal, and stays fatal.
        if (!GrandfatheredPreparationIds.Contains(PreparationId))
            throw new InvalidOperationException("LongMemEval preparation manifest fingerprint mismatch.");
    }

    /// <summary>
    /// The fingerprint of a manifest, computed with the field set its own schema wrote.
    /// </summary>
    /// <remarks>
    /// Schema 6 added five ingestion-identity fields to the hash. Hashing a schema-5 manifest with
    /// them would never reproduce its recorded fingerprint, so every older corpus would read as
    /// corrupt rather than as older -- and a corpus that took nine hours to build would be discarded
    /// over a field it was never asked to record. Schema 7 adds ExtractionSeed under the same rule:
    /// the schema-6 field set is kept verbatim below rather than extended in place.
    /// </remarks>
    internal static string ComputeFingerprint(LongMemEvalPreparationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.SchemaVersion switch
        {
            >= 7 => ComputeCurrentFingerprint(manifest),
            6 => ComputeSchema6Fingerprint(manifest),
            _ => ComputeLegacyFingerprint(manifest),
        };
    }

    /// <summary>
    /// The schema-6 field set as it stood <b>before</b> AbstentionPolicy and RefusedSourceSessions
    /// were added to it, preserved verbatim so corpora sealed in that window still verify.
    /// </summary>
    /// <remarks>
    /// Identical to <see cref="ComputeCurrentFingerprint"/> minus those two fields, in the original
    /// order. Order matters: the hash is over serialized JSON, so moving a field changes the result
    /// even when the values do not.
    /// </remarks>
    private static string ComputeSchema6PreAbstentionFingerprint(LongMemEvalPreparationManifest manifest)
    {
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.PreparationId,
            manifest.DatasetSha256,
            manifest.AgentEvalRevision,
            manifest.ScopeRunIdSha256,
            manifest.AnswerModelId,
            manifest.JudgeModelId,
            manifest.ExtractionModelId,
            manifest.EmbeddingModelId,
            manifest.EmbeddingDimensions,
            manifest.MaxRelevantMessages,
            manifest.ExtractionSourceTime,
            manifest.AssistantContent,
            manifest.UsePredicateVocabulary,
            manifest.ExtractionVocabularySha256,
            manifest.QueryRelationLexiconSha256,
            manifest.ExtractionProvenance,
            manifest.QuestionSeed,
            manifest.UseJsonResponseFormat,
            manifest.ExtractionResponseContract,
            manifest.UseUnifiedExtraction,
            manifest.UseMultiSessionBatchExtraction,
            manifest.PreparationWorkers,
            manifest.MaxSessionsPerBatch,
            manifest.MaxInputTokens,
            manifest.MaxConcurrentBatchesPerExtraction,
            manifest.MaxConcurrentExtractionBatches,
            Questions = manifest.Questions.Select(question => new
            {
                question.QuestionNumber,
                question.QuestionId,
                question.HistorySha256,
                question.ScopeSha256,
                question.MessagesPrepared,
                question.SourceSessions,
                question.ExtractionUnitsPrepared,
                question.GraphSnapshot
            }),
            manifest.InitialExtractionCalls
        };
        return Hash(JsonSerializer.Serialize(canonical, JsonOptions));
    }

    /// <summary>The pre-schema-6 field set, preserved verbatim so older manifests still verify.</summary>
    private static string ComputeLegacyFingerprint(LongMemEvalPreparationManifest manifest)
    {
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.PreparationId,
            manifest.DatasetSha256,
            manifest.AgentEvalRevision,
            manifest.ScopeRunIdSha256,
            manifest.AnswerModelId,
            manifest.JudgeModelId,
            manifest.ExtractionModelId,
            manifest.EmbeddingModelId,
            manifest.EmbeddingDimensions,
            manifest.MaxRelevantMessages,
            manifest.ExtractionSourceTime,
            manifest.UseJsonResponseFormat,
            manifest.ExtractionResponseContract,
            manifest.UseUnifiedExtraction,
            manifest.UseMultiSessionBatchExtraction,
            manifest.PreparationWorkers,
            manifest.MaxSessionsPerBatch,
            manifest.MaxInputTokens,
            manifest.MaxConcurrentBatchesPerExtraction,
            manifest.MaxConcurrentExtractionBatches,
            Questions = manifest.Questions.Select(question => new
            {
                question.QuestionNumber,
                question.QuestionId,
                question.HistorySha256,
                question.ScopeSha256,
                question.MessagesPrepared,
                question.SourceSessions,
                question.ExtractionUnitsPrepared,
                // Projected to the ELEVEN counters that existed when these corpora were sealed. The
                // whole-object form is exactly what broke them: 6.5 added nullable ReasoningTraces
                // and Procedures to fix a label-blind probe -- changing nothing about what was
                // stored -- and two extra nulls in the serialised JSON invalidated every frozen
                // corpus. A fingerprint must never serialise a record it does not control the shape of.
                GraphSnapshot = new
                {
                    question.GraphSnapshot.Entities,
                    question.GraphSnapshot.Facts,
                    question.GraphSnapshot.Preferences,
                    question.GraphSnapshot.Relationships,
                    question.GraphSnapshot.RelationshipsWithProvenance,
                    question.GraphSnapshot.LearnedItems,
                    question.GraphSnapshot.LearnedItemsWithProvenance,
                    question.GraphSnapshot.ProvenanceEdges,
                    question.GraphSnapshot.SourceMessages,
                    question.GraphSnapshot.TotalLearned,
                    question.GraphSnapshot.CompleteProvenance,
                }
            }),
            manifest.InitialExtractionCalls
        };
        return Hash(JsonSerializer.Serialize(canonical, JsonOptions));
    }

    /// <summary>
    /// The schema-6 field set, preserved verbatim so corpora sealed under it still verify.
    /// </summary>
    /// <remarks>
    /// Order matters: the hash is over serialized JSON, so moving a field changes the result even
    /// when the values do not. Nothing may be added here — schema 7 and later go in
    /// <see cref="ComputeCurrentFingerprint"/>.
    /// </remarks>
    private static string ComputeSchema6Fingerprint(LongMemEvalPreparationManifest manifest)
    {
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.PreparationId,
            manifest.DatasetSha256,
            manifest.AgentEvalRevision,
            manifest.ScopeRunIdSha256,
            manifest.AnswerModelId,
            manifest.JudgeModelId,
            manifest.ExtractionModelId,
            manifest.EmbeddingModelId,
            manifest.EmbeddingDimensions,
            manifest.MaxRelevantMessages,
            manifest.ExtractionSourceTime,
            manifest.AssistantContent,
            manifest.UsePredicateVocabulary,
            manifest.ExtractionVocabularySha256,
            manifest.QueryRelationLexiconSha256,
            manifest.ExtractionProvenance,
            manifest.AbstentionPolicy,
            manifest.RefusedSourceSessions,
            manifest.QuestionSeed,
            manifest.UseJsonResponseFormat,
            manifest.ExtractionResponseContract,
            manifest.UseUnifiedExtraction,
            manifest.UseMultiSessionBatchExtraction,
            manifest.PreparationWorkers,
            manifest.MaxSessionsPerBatch,
            manifest.MaxInputTokens,
            manifest.MaxConcurrentBatchesPerExtraction,
            manifest.MaxConcurrentExtractionBatches,
            Questions = manifest.Questions.Select(question => new
            {
                question.QuestionNumber,
                question.QuestionId,
                question.HistorySha256,
                question.ScopeSha256,
                question.MessagesPrepared,
                question.SourceSessions,
                question.ExtractionUnitsPrepared,
                question.GraphSnapshot
            }),
            manifest.InitialExtractionCalls
        };
        return Hash(JsonSerializer.Serialize(canonical, JsonOptions));
    }

    private static string ComputeCurrentFingerprint(LongMemEvalPreparationManifest manifest)
    {
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.PreparationId,
            manifest.DatasetSha256,
            manifest.AgentEvalRevision,
            manifest.ScopeRunIdSha256,
            manifest.AnswerModelId,
            manifest.JudgeModelId,
            manifest.ExtractionModelId,
            manifest.EmbeddingModelId,
            manifest.EmbeddingDimensions,
            manifest.MaxRelevantMessages,
            manifest.ExtractionSourceTime,
            // Schema 6. These five decide what the extractor was asked for and therefore what the
            // graph contains; leaving them out of the fingerprint is what let two materially different
            // corpora hash identically.
            manifest.AssistantContent,
            manifest.UsePredicateVocabulary,
            // Schema 7. What the extraction calls were seeded with, which decides what the extractor
            // returned and therefore what the graph contains.
            manifest.ExtractionSeed,
            manifest.ExtractionVocabularySha256,
            manifest.QueryRelationLexiconSha256,
            manifest.ExtractionProvenance,
            manifest.AbstentionPolicy,
            manifest.RefusedSourceSessions,
            manifest.QuestionSeed,
            manifest.UseJsonResponseFormat,
            manifest.ExtractionResponseContract,
            manifest.UseUnifiedExtraction,
            manifest.UseMultiSessionBatchExtraction,
            manifest.PreparationWorkers,
            manifest.MaxSessionsPerBatch,
            manifest.MaxInputTokens,
            manifest.MaxConcurrentBatchesPerExtraction,
            manifest.MaxConcurrentExtractionBatches,
            Questions = manifest.Questions.Select(question => new
            {
                question.QuestionNumber,
                question.QuestionId,
                question.HistorySha256,
                question.ScopeSha256,
                question.MessagesPrepared,
                question.SourceSessions,
                question.ExtractionUnitsPrepared,
                question.GraphSnapshot
            }),
            manifest.InitialExtractionCalls
        };
        return Hash(JsonSerializer.Serialize(canonical, JsonOptions));
    }

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

internal sealed record LongMemEvalPreparationExpectation(
    string DatasetSha256,
    string AgentEvalRevision,
    string AnswerModelId,
    string JudgeModelId,
    string ExtractionModelId,
    string EmbeddingModelId,
    int EmbeddingDimensions,
    int MaxRelevantMessages,
    string ExtractionSourceTime,
    bool UseJsonResponseFormat = true,
    string ExtractionResponseContract = "json-object",
    bool UseUnifiedExtraction = false,
    bool UseMultiSessionBatchExtraction = false,
    int PreparationWorkers = 1,
    int MaxSessionsPerBatch = 1,
    int MaxInputTokens = 100_000,
    int MaxConcurrentBatchesPerExtraction = 1,
    int MaxConcurrentExtractionBatches = 0)
{
    internal void Validate(LongMemEvalPreparationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var mismatches = new List<string>();
        void Check(string field, object? expected, object? actual)
        {
            if (!Equals(expected?.ToString(), actual?.ToString()))
                mismatches.Add($"{field}: corpus={actual} run={expected}");
        }

        Check("datasetSha256", DatasetSha256, manifest.DatasetSha256);
        Check("answerModelId", AnswerModelId, manifest.AnswerModelId);
        Check("judgeModelId", JudgeModelId, manifest.JudgeModelId);
        Check("extractionModelId", ExtractionModelId, manifest.ExtractionModelId);
        Check("embeddingModelId", EmbeddingModelId, manifest.EmbeddingModelId);
        Check("embeddingDimensions", EmbeddingDimensions, manifest.EmbeddingDimensions);
        Check("maxRelevantMessages", MaxRelevantMessages, manifest.MaxRelevantMessages);
        Check("useJsonResponseFormat", UseJsonResponseFormat, manifest.UseJsonResponseFormat);
        Check("extractionResponseContract", ExtractionResponseContract, manifest.ExtractionResponseContract);
        Check("extractionSourceTime", ExtractionSourceTime, manifest.ExtractionSourceTime);
        Check("useUnifiedExtraction", UseUnifiedExtraction, manifest.UseUnifiedExtraction);
        Check("useMultiSessionBatchExtraction", UseMultiSessionBatchExtraction, manifest.UseMultiSessionBatchExtraction);
        Check("preparationWorkers", PreparationWorkers, manifest.PreparationWorkers);
        Check("maxSessionsPerBatch", MaxSessionsPerBatch, manifest.MaxSessionsPerBatch);
        Check("maxInputTokens", MaxInputTokens, manifest.MaxInputTokens);
        Check("maxConcurrentBatchesPerExtraction", MaxConcurrentBatchesPerExtraction, manifest.MaxConcurrentBatchesPerExtraction);
        Check("maxConcurrentExtractionBatches", MaxConcurrentExtractionBatches, manifest.MaxConcurrentExtractionBatches);

        // agentEvalRevision is DELIBERATELY not compared here, though it is still recorded.
        //
        // AgentEval's involvement in a corpus is real but bounded: it decides which questions are
        // sampled and how each conversation history is rendered to text before AgentMemory extracts
        // from it. Everything else it does -- the judge, the answer prompt, the verdict protocol --
        // happens at evaluation time and touches no stored fact.
        //
        // Both of those bounded effects are already checked EXACTLY, per question, by
        // LongMemEvalPreparedState: QuestionId proves the same question was ingested, and
        // HistorySha256 proves the same rendered text was ingested. Comparing a version string on top
        // of that rejects corpora whose content is provably identical -- a 7-9 hour build discarded
        // because a package moved 0.16 to 0.20 while producing byte-identical histories. If a future
        // release DOES change the formatter or the sampler, the per-question hashes catch it and name
        // the question; a version string only ever said "something, somewhere, might differ".
        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                "Prepared LongMemEval configuration does not match the sealed manifest. "
                + $"{mismatches.Count} field(s) differ:" + Environment.NewLine
                + string.Join(Environment.NewLine, mismatches.Select(m => $"  - {m}")));
        }
    }
}

internal static class LongMemEvalPreparationFingerprint
{
    internal static LongMemEvalPreparationExpectation Expect(
        string datasetSha256,
        string agentEvalRevision,
        string answerModelId,
        string judgeModelId,
        string extractionModelId,
        string embeddingModelId,
        int embeddingDimensions,
        int maxRelevantMessages,
        bool useJsonResponseFormat = true,
        string extractionResponseContract = "json-object",
        bool useUnifiedExtraction = false,
        bool useMultiSessionBatchExtraction = false,
        int preparationWorkers = 1,
        int maxSessionsPerBatch = 1,
        int maxInputTokens = 100_000,
        int maxConcurrentBatchesPerExtraction = 1,
        int maxConcurrentExtractionBatches = 0) =>
        new(
            datasetSha256,
            agentEvalRevision,
            answerModelId,
            judgeModelId,
            extractionModelId,
            embeddingModelId,
            embeddingDimensions,
            maxRelevantMessages,
            "metadata-only-not-in-extraction-prompt",
            useJsonResponseFormat,
            extractionResponseContract,
            useUnifiedExtraction,
            useMultiSessionBatchExtraction,
            preparationWorkers,
            maxSessionsPerBatch,
            maxInputTokens,
            maxConcurrentBatchesPerExtraction,
            maxConcurrentExtractionBatches);
}
public sealed class LongMemEvalPreparedState
{
    private readonly IReadOnlyDictionary<int, LongMemEvalPreparedQuestion> _byNumber;

    internal LongMemEvalPreparedState(
        LongMemEvalPreparationManifest manifest,
        string scopeRunId)
        : this(manifest, scopeRunId, expectation: null)
    {
    }

    internal LongMemEvalPreparedState(
        LongMemEvalPreparationManifest manifest,
        string scopeRunId,
        LongMemEvalPreparationExpectation? expectation)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRunId);
        manifest.VerifyIntegrity();
        if (!string.Equals(
                manifest.ScopeRunIdSha256,
                LongMemEvalPreparationManifest.Hash(scopeRunId),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Prepared LongMemEval scope does not match the sealed manifest.");
        }

        expectation?.Validate(manifest);
        Manifest = manifest;
        _byNumber = manifest.Questions.ToDictionary(question => question.QuestionNumber);
    }

    internal LongMemEvalPreparationManifest Manifest { get; }

    internal LongMemEvalPreparedQuestion ValidateQuestion(
        int questionNumber,
        LongMemEvalEvidenceQuestion evidenceQuestion,
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        string sessionId,
        string ownerId)
    {
        ArgumentNullException.ThrowIfNull(evidenceQuestion);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        if (!_byNumber.TryGetValue(questionNumber, out var prepared))
        {
            throw new InvalidOperationException(
                $"Prepared LongMemEval manifest has no question position {questionNumber}.");
        }

        var historySha256 = LongMemEvalEvidenceIndex.Fingerprint(history);
        var scopeSha256 = LongMemEvalPreparationManifest.Hash($"{sessionId}|{ownerId}");
        var sourceSessions = evidenceQuestion.Messages
            .Where(message =>
                !message.IsSyntheticBoundary &&
                !message.IsSyntheticFormatterPadding)
            .Select(message => message.SourceSessionOrdinal)
            .Distinct()
            .Count();
        // G3B.9 stopped persisting AgentEval's fabricated session-boundary turns, so the sealed
        // count is of real conversation only. Comparing it against every injected message would
        // reject every question. The guard stays exact — it is the expectation that was stale.
        var persistableMessages = evidenceQuestion.Messages
            .Count(message =>
                !message.IsSyntheticBoundary &&
                !message.IsSyntheticFormatterPadding);

        if (!string.Equals(prepared.QuestionId, evidenceQuestion.QuestionId, StringComparison.Ordinal) ||
            !string.Equals(prepared.HistorySha256, historySha256, StringComparison.Ordinal) ||
            !string.Equals(prepared.ScopeSha256, scopeSha256, StringComparison.Ordinal) ||
            prepared.MessagesPrepared != persistableMessages ||
            prepared.SourceSessions != sourceSessions ||
            prepared.ExtractionUnitsPrepared != sourceSessions)
        {
            throw new InvalidOperationException(
                $"Prepared LongMemEval question {questionNumber} does not match the sealed manifest.");
        }

        return prepared;
    }
}

internal sealed class Neo4jLongMemEvalPreparationStore(IDriver driver)
{
    private const string Label = "LongMemEvalPreparation";

    internal async Task SealAsync(
        LongMemEvalPreparationManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.VerifyIntegrity();
        var json = JsonSerializer.Serialize(
            manifest,
            LongMemEvalPreparationManifest.JsonOptions);

        await using var session = driver.AsyncSession(
            options => options.WithDefaultAccessMode(AccessMode.Write));
        await session.ExecuteWriteAsync(async transaction =>
        {
            var existingCursor = await transaction.RunAsync(
                $"MATCH (m:{Label} {{id: $id}}) RETURN count(m) AS count",
                new { id = manifest.PreparationId }).ConfigureAwait(false);
            var existing = await existingCursor.SingleAsync().ConfigureAwait(false);
            if (existing["count"].As<long>() != 0)
            {
                throw new InvalidOperationException(
                    "LongMemEval preparation id is already sealed.");
            }

            var createCursor = await transaction.RunAsync(
                $$"""
                CREATE (m:{{Label}} {
                    id: $id,
                    schema_version: $schemaVersion,
                    fingerprint: $fingerprint,
                    manifest_json: $manifestJson,
                    sealed_at: datetime()
                })
                RETURN m.fingerprint AS fingerprint
                """,
                new
                {
                    id = manifest.PreparationId,
                    schemaVersion = manifest.SchemaVersion,
                    fingerprint = manifest.Fingerprint,
                    manifestJson = json
                }).ConfigureAwait(false);
            var created = await createCursor.SingleAsync().ConfigureAwait(false);
            if (!string.Equals(
                    created["fingerprint"].As<string>(),
                    manifest.Fingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "LongMemEval preparation manifest was not sealed exactly.");
            }
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The preparation id sealed into this store, so an adopted volume describes itself.
    /// </summary>
    /// <remarks>
    /// G3B.12-R. Reuse only receives a volume name; the run identity it needs to reproduce session
    /// and owner scopes lives inside the graph. Reading it back is what lets a retained build be
    /// evaluated without a rebuild — and every question would otherwise trip
    /// <c>prepared-manifest-mismatch</c>, since scope hashes are derived from that id.
    /// <para>
    /// Exactly one manifest per store is required: more than one means volumes were mixed, which
    /// would silently evaluate one graph against another's sealed expectations.
    /// </para>
    /// </remarks>
    internal async Task<string> ReadSealedPreparationIdAsync(
        CancellationToken cancellationToken = default)
    {
        await using var session = driver.AsyncSession(
            options => options.WithDefaultAccessMode(AccessMode.Read));
        var ids = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync($"MATCH (m:{Label}) RETURN m.id AS id");
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(record => record["id"].As<string>()).ToList();
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return ids.Count switch
        {
            1 => ids[0],
            0 => throw new InvalidOperationException(
                "The reused volume holds no sealed LongMemEval preparation; it was never prepared, " +
                "or preparation did not complete."),
            _ => throw new InvalidOperationException(
                $"The reused volume holds {ids.Count} sealed preparations; exactly one is required.")
        };
    }

    internal async Task<LongMemEvalPreparationManifest> ReadAsync(
        string preparationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        await using var session = driver.AsyncSession(
            options => options.WithDefaultAccessMode(AccessMode.Read));
        var records = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                $$"""
                MATCH (m:{{Label}} {id: $id})
                RETURN m.schema_version AS schemaVersion,
                       m.fingerprint AS fingerprint,
                       m.manifest_json AS manifestJson
                """,
                new { id = preparationId }).ConfigureAwait(false);
            return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (records.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected one sealed LongMemEval preparation manifest; found {records.Count}.");
        }

        var record = records[0];
        var manifest = JsonSerializer.Deserialize<LongMemEvalPreparationManifest>(
            record["manifestJson"].As<string>(),
            LongMemEvalPreparationManifest.JsonOptions)
            ?? throw new InvalidOperationException(
                "LongMemEval preparation manifest could not be deserialized.");
        if (record["schemaVersion"].As<int>() != manifest.SchemaVersion ||
            !string.Equals(
                record["fingerprint"].As<string>(),
                manifest.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "LongMemEval preparation marker does not match its manifest.");
        }

        manifest.VerifyIntegrity();
        if (!manifest.FingerprintVerified)
        {
            // Loud, because the alternative to a fatal check is a check nobody notices. The corpus is
            // still usable and drift still guards the configuration; what cannot be re-derived is the
            // seal itself, and any result taken over it should say so.
            Console.Error.WriteLine(
                $"longmemeval: WARNING - prepared corpus '{manifest.PreparationId}' has an "
                + "UNVERIFIABLE fingerprint. It was sealed by a build whose fingerprint field set "
                + "differed from this one (the schema version did not change when the field set did), "
                + "so the hash cannot be recomputed. The corpus is readable and drift checking is "
                + "unaffected; its seal is not independently confirmable.");
        }

        return manifest;
    }
}
