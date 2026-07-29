using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

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
