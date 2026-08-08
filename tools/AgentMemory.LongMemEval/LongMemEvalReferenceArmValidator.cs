using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

internal sealed record LongMemEvalReferenceArmValidation(
    bool Accepted,
    IReadOnlyList<string> Issues,
    int SkippedQuestions,
    int AnsweredQuestions,
    int CorrectQuestions,
    double? FittedAccuracyPercent);

/// <summary>
/// G4-REF acceptance. A reference arm has zero storage and zero recall <i>by design</i>, so it gets
/// its own exact contract rather than an exemption carved into
/// <see cref="LongMemEvalRunValidator"/> — which stays untouched and un-relaxed.
/// </summary>
internal static class LongMemEvalReferenceArmValidator
{
    internal static LongMemEvalReferenceArmValidation Validate(
        int questionCount,
        int llmCalls,
        IReadOnlyList<LongMemEvalReferenceTelemetry> telemetry,
        IReadOnlyList<QuestionResult> questionResults,
        LongMemEvalChatCallSnapshot? answerCalls = null,
        LongMemEvalChatCallSnapshot? judgeCalls = null,
        int diagnosticJudgeCalls = 0,
        int agentEvalJudgeRetryAllowance = 0)
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

        if (telemetry.Count != questionCount)
        {
            issues.Add(
                $"The reference arm recorded {telemetry.Count} question telemetry entries for {questionCount} AgentEval results.");
        }

        // Same contract as the memory arms — and it must track them. AgentEval retries an
        // unparseable judge verdict internally under JudgeFailurePolicy.RetryThenInconclusive without
        // reporting how many times, so an exact count is unachievable from outside the library. This
        // validator kept the exact form after the memory-arm validator moved to a range, and a real
        // no-memory floor run was discarded for it (12 judge calls over 10 questions). Correctness
        // stays exact below: one answer call per question, one valid verdict per question.
        var minimumCalls = questionCount * 2;
        var maximumCalls = questionCount * (2 + agentEvalJudgeRetryAllowance);
        var baseLlmCalls = llmCalls - diagnosticJudgeCalls;
        if (baseLlmCalls < minimumCalls || baseLlmCalls > maximumCalls)
        {
            issues.Add(
                $"AgentEval reported {llmCalls} LLM calls ({baseLlmCalls} base after excluding " +
                $"{diagnosticJudgeCalls} diagnostic judge retries) for {questionCount} questions; " +
                $"expected between {minimumCalls} and {maximumCalls} base calls " +
                $"({agentEvalJudgeRetryAllowance} internal judge retries permitted per question).");
        }

        if (answerCalls is not null && answerCalls.Calls != questionCount)
        {
            issues.Add(
                $"Observed {answerCalls.Calls} answer calls for {questionCount} questions; expected exactly {questionCount}.");
        }

        var baseJudgeCalls = (judgeCalls?.Calls ?? 0) - diagnosticJudgeCalls;
        if (judgeCalls is not null &&
            (baseJudgeCalls < questionCount ||
             baseJudgeCalls > questionCount * (1 + agentEvalJudgeRetryAllowance)))
        {
            issues.Add(
                $"Observed {judgeCalls.Calls} judge calls ({baseJudgeCalls} base " +
                $"after excluding {diagnosticJudgeCalls} diagnostic retries) for {questionCount} questions; " +
                $"expected between {questionCount} and {questionCount * (1 + agentEvalJudgeRetryAllowance)} " +
                "base judge calls.");
        }

        var skipped = telemetry.Count(item =>
            string.Equals(item.Status, "skipped-context-window", StringComparison.Ordinal));

        foreach (var unexpected in telemetry.Where(item =>
                     !string.Equals(item.Status, "completed", StringComparison.Ordinal) &&
                     !string.Equals(item.Status, "skipped-context-window", StringComparison.Ordinal)))
        {
            issues.Add(
                $"The reference arm recorded {unexpected.Status} at question position {unexpected.QuestionNumber}.");
        }

        // The only provider failure a reference arm may carry is a context-window rejection, and
        // exactly as many as it recorded as skips. One extra means something else broke.
        if (answerCalls is not null && answerCalls.Failures != skipped)
        {
            issues.Add(
                $"Observed {answerCalls.Failures} failed answer calls against {skipped} recorded " +
                "context-window skips; a reference arm may only fail by exceeding the context window.");
        }

        if (judgeCalls is not null && judgeCalls.Failures != 0)
            issues.Add($"Observed {judgeCalls.Failures} failed judge provider calls; expected zero.");

        var correct = 0;
        var answered = 0;
        var skippedIds = telemetry
            .Where(item => string.Equals(item.Status, "skipped-context-window", StringComparison.Ordinal))
            .Select(item => item.QuestionId)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var question in questionResults)
        {
            var response = question.AgentResponse ?? string.Empty;
            var explanation = question.JudgeExplanation ?? string.Empty;
            var isSkipped =
                response.StartsWith(LongMemEvalReferenceAgent.SkippedAnswer, StringComparison.Ordinal) ||
                skippedIds.Contains(question.QuestionId);

            if (response.StartsWith("[ERROR:", StringComparison.OrdinalIgnoreCase) ||
                response.StartsWith("[CONTENT_FILTER]", StringComparison.OrdinalIgnoreCase) ||
                explanation.StartsWith("Skipped due to error:", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Agent invocation failed before judging question {question.QuestionId}.");
                continue;
            }

            if (explanation.StartsWith("Judge error:", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"AgentEval judge failed for question {question.QuestionId}.");
                continue;
            }

            if (!LongMemEvalRunValidator.TryParseJudgeVerdict(explanation, out var judgedCorrect))
            {
                issues.Add(
                    $"AgentEval judge returned no valid yes/no verdict for question {question.QuestionId}.");
                continue;
            }

            if (question.Correct != judgedCorrect)
            {
                issues.Add(
                    $"AgentEval judge verdict and recorded correctness disagree for question {question.QuestionId}.");
            }

            // A skipped question is excluded from the score rather than counted wrong. Scoring it
            // zero would report "the ceiling is low" when the truth is "the ceiling was not
            // measurable on this deployment".
            if (isSkipped)
                continue;
            answered++;
            if (judgedCorrect)
                correct++;
        }

        return new LongMemEvalReferenceArmValidation(
            Accepted: issues.Count == 0,
            Issues: issues.AsReadOnly(),
            SkippedQuestions: skipped,
            AnsweredQuestions: answered,
            CorrectQuestions: correct,
            FittedAccuracyPercent: answered == 0 ? null : 100d * correct / answered);
    }
}
