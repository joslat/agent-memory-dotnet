using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

internal sealed record LongMemEvalRunValidation(
    bool Accepted,
    IReadOnlyList<string> Issues);

internal static class LongMemEvalRunValidator
{
    internal static LongMemEvalRunValidation Validate(
        int questionCount,
        int llmCalls,
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry,
        IReadOnlyList<QuestionResult> questionResults)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(questionResults);
        var issues = new List<string>();

        if (questionCount == 0)
            issues.Add("AgentEval returned no LongMemEval questions.");

        if (questionResults.Count != questionCount)
        {
            issues.Add(
                $"AgentEval returned {questionResults.Count} question results for {questionCount} questions.");
        }

        var expectedCalls = questionCount * 2;
        if (llmCalls != expectedCalls)
        {
            issues.Add(
                $"AgentEval reported {llmCalls} LLM calls for {questionCount} questions; expected exactly {expectedCalls}.");
        }

        if (telemetry.Count != questionCount)
        {
            issues.Add(
                $"AgentMemory recorded {telemetry.Count} question telemetry entries for {questionCount} AgentEval results.");
        }

        if (telemetry.Any(item => item.MessagesStored == 0 || item.ItemsRetrieved == 0))
        {
            issues.Add(
                "At least one LongMemEval question bypassed AgentMemory storage or retrieved no items.");
        }

        foreach (var failedStage in telemetry.Where(item =>
                     !string.Equals(item.Status, "completed", StringComparison.Ordinal)))
        {
            issues.Add(
                $"AgentMemory recorded {failedStage.Status} at question position {failedStage.QuestionNumber}.");
        }

        foreach (var question in questionResults)
        {
            var response = question.AgentResponse ?? string.Empty;
            var explanation = question.JudgeExplanation ?? string.Empty;
            if (response.StartsWith("[ERROR:", StringComparison.OrdinalIgnoreCase) ||
                response.StartsWith("[CONTENT_FILTER]", StringComparison.OrdinalIgnoreCase) ||
                explanation.StartsWith("Skipped due to error:", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(
                    $"Agent invocation failed before judging question {question.QuestionId}.");
                continue;
            }

            if (explanation.StartsWith("Judge error:", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(
                    $"AgentEval judge failed for question {question.QuestionId}.");
            }
        }

        return new LongMemEvalRunValidation(
            Accepted: issues.Count == 0,
            Issues: issues.AsReadOnly());
    }

    internal static string Classify(
        QuestionResult question,
        LongMemEvalQuestionTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (telemetry is not null &&
            !string.Equals(telemetry.Status, "completed", StringComparison.Ordinal))
            return telemetry.Status;

        var response = question.AgentResponse ?? string.Empty;
        var explanation = question.JudgeExplanation ?? string.Empty;

        foreach (var stage in new[] { "storage", "retrieval", "answer" })
        {
            if (response.Contains($"LongMemEval {stage} stage failed.", StringComparison.OrdinalIgnoreCase))
                return $"{stage}-error";
        }

        if (response.StartsWith("[ERROR:", StringComparison.OrdinalIgnoreCase) ||
            response.StartsWith("[CONTENT_FILTER]", StringComparison.OrdinalIgnoreCase) ||
            explanation.StartsWith("Skipped due to error:", StringComparison.OrdinalIgnoreCase))
            return "agent-error";
        if (explanation.StartsWith("Judge error:", StringComparison.OrdinalIgnoreCase))
            return "judge-error";
        return "completed";
    }
}
