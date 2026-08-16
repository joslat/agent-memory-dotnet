using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Wall-clock stages of one prepared-pair run. <see cref="ManifestSealAndReadBackMs"/> is nullable
/// because a reused run seals nothing, and reporting an unperformed stage as 0 ms would read as a
/// measurement of instant work rather than of work that never happened.
/// </summary>
internal sealed record LongMemEvalPreparationTimings(
    double ProfileStartupMs,
    double? ManifestSealAndReadBackMs,
    double BaseVolumeStopMs,
    double StructuredCloneMs,
    double HybridCloneMs);

internal static class LongMemEvalReportProjection
{
    public static object CreateAcceptedResult(
        ExternalBenchmarkResult result,
        LongMemEvalEvidenceDetail evidenceDetail)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (evidenceDetail == LongMemEvalEvidenceDetail.Content)
            return result;

        return new
        {
            result.BenchmarkId,
            result.BenchmarkName,
            result.OverallAccuracy,
            result.TaskAveragedAccuracy,
            result.PerTypeResults,
            QuestionResults = result.QuestionResults.Select(question => new
            {
                question.QuestionId,
                question.QuestionType,
                question.Correct,
                question.RawScore,
                question.Duration,
                Evidence = evidenceDetail == LongMemEvalEvidenceDetail.Identifiers &&
                    question.Evidence is not null
                        ? ProjectEvidence(question.Evidence) : null,
                EvidenceDiagnostics = evidenceDetail == LongMemEvalEvidenceDetail.Identifiers &&
                    question.EvidenceDiagnostics is not null
                        ? ProjectDiagnostics(question.EvidenceDiagnostics) : null
            }),
            result.Duration,
            result.TotalLlmCalls,
            result.EstimatedCostUsd
        };
    }

    private static object ProjectEvidence(QuestionEvidenceEnvelope evidence) => new
    {
        evidence.SchemaVersion,
        Retrieved = evidence.Retrieved.Select(ProjectReference),
        AnswerContext = evidence.AnswerContext.Select(ProjectReference)
    };

    private static object ProjectReference(EvidenceReference reference) => new
    {
        reference.Id,
        reference.Rank,
        reference.SimilarityScore,
        reference.SourceSessionId,
        reference.SourceTurnIndex,
        reference.SourceTimestamp,
        reference.AnswerContextOrder
    };

    /// <summary>
    /// The prepared-pair report's <c>preparation</c> section.
    /// </summary>
    /// <remarks>
    /// Extracted from the inline report so the reuse path can be covered by a test. A run started
    /// with <c>--reuse-prepared-volumes</c> performs no preparation at all, so
    /// <paramref name="batchExecution"/> is null for it.
    /// </remarks>
    internal static object CreatePreparationSection(
        LongMemEvalPreparationManifest manifest,
        LongMemEvalPreparedBatchExecution? batchExecution,
        IReadOnlyList<LongMemEvalQuestionTelemetry> preparationTelemetry,
        object extractionObserved,
        long extractionCalls,
        LongMemEvalPreparationTimings timings,
        string? reusedPreparedVolume)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(preparationTelemetry);
        ArgumentNullException.ThrowIfNull(timings);

        return new
        {
            count = 1,
            manifest.SchemaVersion,
            manifest.PreparationId,
            manifest.Fingerprint,
            manifest.DatasetSha256,
            manifest.AgentEvalRevision,
            manifest.MessagesPrepared,
            manifest.ExtractionUnitsPrepared,
            manifest.InitialExtractionCalls,
            manifest.UseUnifiedExtraction,
            manifest.UseMultiSessionBatchExtraction,
            manifest.PreparationWorkers,
            manifest.MaxSessionsPerBatch,
            manifest.MaxInputTokens,
            manifest.MaxConcurrentBatchesPerExtraction,
            manifest.MaxConcurrentExtractionBatches,
            // ── Ingestion identity (schema 6) ────────────────────────────────────────────────
            // The manifest has recorded these since schema 6 and the REPORT did not project any of
            // them, which defeats most of the point: the report is the artifact a human reads and
            // compares, so two corpora built under materially different ingestion settings -- an
            // Utterance arm and an Ignore arm, say -- projected identically in every visible field.
            // Found while trying to decide 8.3b from artifacts on disk and being unable to tell which
            // arm a recorded run belonged to. The fingerprint would have differed, but a fingerprint
            // says "not the same", never "differs in the episodic mode".
            manifest.AssistantContent,
            manifest.UsePredicateVocabulary,
            // Schema 7 (30.1): null means the corpus was built with no extraction seed, which is what
            // every corpus predating schema 7 was.
            manifest.ExtractionSeed,
            manifest.ExtractionVocabularySha256,
            manifest.QueryRelationLexiconSha256,
            manifest.ExtractionProvenance,
            manifest.AbstentionPolicy,
            manifest.RefusedSourceSessions,
            manifest.MemoryTypes,
            manifest.QuestionSeed,
            manifest.Description,
            // Observed rather than configured, and outside the fingerprint for that reason (S-4).
            manifest.ExtractionProviderBuilds,
            performedByThisRun = batchExecution is not null,
            reusedPreparedVolume,
            plannedEstimatedInputTokens =
                batchExecution?.EstimatedInputTokens,
            maximumObservedConcurrency =
                batchExecution?.MaximumConcurrency,
            questions = manifest.Questions,
            extractionObserved,
            extractionRetryCalls =
                Math.Max(0, extractionCalls - manifest.InitialExtractionCalls),
            timings = new
            {
                profileStartupMs = timings.ProfileStartupMs,
                storageAndEmbeddingMs = preparationTelemetry.Sum(item =>
                    item.StageTimings?.StorageMs ?? 0),
                extractionAndPersistenceMs = preparationTelemetry.Sum(item =>
                    item.StageTimings?.ExtractionPersistenceMs ?? 0),
                graphReadBackMs = preparationTelemetry.Sum(item =>
                    item.StageTimings?.GraphReadBackMs ?? 0),
                manifestSealAndReadBackMs = timings.ManifestSealAndReadBackMs,
                baseVolumeStopMs = timings.BaseVolumeStopMs,
                structuredCloneMs = timings.StructuredCloneMs,
                hybridCloneMs = timings.HybridCloneMs
            }
        };
    }

    private static object ProjectDiagnostics(
        QuestionEvidenceDiagnostics diagnostics) => new
        {
            diagnostics.Status,
            diagnostics.SafeFailureCode,
            diagnostics.RetrievedReferenceCount,
            diagnostics.AnswerContextReferenceCount,
            diagnostics.GoldSessionPresent,
            diagnostics.HasAnswerTurnPresent,
            diagnostics.FirstGoldRank,
            diagnostics.DistinctSourceSessionCount,
            diagnostics.SourceSessionDiversityRatio,
            diagnostics.AnswerContextOrders,
            diagnostics.AnswerContextTimestampCount
        };
}
