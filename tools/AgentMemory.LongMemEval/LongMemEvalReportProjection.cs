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
                question.Duration
            }),
            result.Duration,
            result.TotalLlmCalls,
            result.EstimatedCostUsd
        };
    }
}
