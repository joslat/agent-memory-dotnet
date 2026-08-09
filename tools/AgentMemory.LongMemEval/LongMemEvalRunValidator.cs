using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

internal sealed record LongMemEvalRunValidation(
    bool Accepted,
    IReadOnlyList<string> Issues);

internal static class LongMemEvalRunValidator
{
    /// <summary>
    /// Whether a question retrieved nothing as a <b>measured result</b> rather than a broken run.
    /// </summary>
    /// <remarks>
    /// Delegates the judgement to <see cref="AgentMemoryLongMemEvalAdapter.CanScoreEmptyRetrieval"/>
    /// rather than re-deriving it. The validator enforces this rule independently of the adapter, so
    /// two copies of the reasoning would drift — and the drift would look like an accepted run on one
    /// side and a rejected one on the other.
    /// <para>
    /// The status must be exactly <c>retrieval-empty</c>. A <c>storage-error</c> or a
    /// <c>graph-readback-empty</c> against a populated graph is still a broken run; only "retrieval
    /// ran and returned nothing" is a result worth scoring.
    /// </para>
    /// </remarks>
    internal static bool IsScoredEmptyRetrieval(string? status, LongMemEvalGraphSnapshot? graphSnapshot) =>
        string.Equals(status, "retrieval-empty", StringComparison.Ordinal) &&
        AgentMemoryLongMemEvalAdapter.CanScoreEmptyRetrieval(graphSnapshot);

    internal static LongMemEvalRunValidation Validate(
        int questionCount,
        int llmCalls,
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry,
        IReadOnlyList<QuestionResult> questionResults,
        LongMemEvalChatCallSnapshot? answerCalls = null,
        LongMemEvalChatCallSnapshot? judgeCalls = null,
        LongMemEvalChatCallSnapshot? extractionCalls = null,
        long expectedInitialExtractionCalls = 0,
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

        // Diagnostic judge retries are deliberately additional calls that never rewrite a base
        // verdict (the report records diagnosticCallsAffectScore = false), so they are excluded from
        // the exact 2N base-call contract rather than being allowed to reject an otherwise valid run.
        // The guard itself is unchanged: base calls must still be exactly 2N.
        // AgentEval retries an unparseable judge verdict *internally* under
        // JudgeFailurePolicy.RetryThenInconclusive and does not report how many times, so an exact
        // call count is not achievable from outside the library. The correctness property is kept
        // exact instead — one answer call per question, and one valid verdict per question, both
        // asserted below — while the call count becomes a bounded cost signal. A run that exceeds
        // the configured retry allowance still rejects, so runaway judging cannot pass.
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

        if (telemetry.Count != questionCount)
        {
            issues.Add(
                $"AgentMemory recorded {telemetry.Count} question telemetry entries for {questionCount} AgentEval results.");
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

        if (answerCalls is not null && judgeCalls is not null &&
            answerCalls.Calls + judgeCalls.Calls != llmCalls + diagnosticJudgeCalls)
        {
            issues.Add(
                $"Observed answer and judge calls total {answerCalls.Calls + judgeCalls.Calls}, but AgentEval reported {llmCalls}.");
        }

        if (extractionCalls is not null && extractionCalls.Calls < expectedInitialExtractionCalls)
        {
            issues.Add(
                $"Observed {extractionCalls.Calls} extraction calls; expected at least {expectedInitialExtractionCalls} initial calls.");
        }

        var providerFailures =
            (answerCalls?.Failures ?? 0) +
            (judgeCalls?.Failures ?? 0) +
            (extractionCalls?.Failures ?? 0);
        if (providerFailures != 0)
        {
            issues.Add(
                $"Observed {providerFailures} failed answer, judge, or extraction provider calls.");
        }


        var preparedQuestions = telemetry.Count(item => item.PreparedMemory);
        if (preparedQuestions != 0 && preparedQuestions != telemetry.Count)
        {
            issues.Add(
                "Prepared and independently ingested LongMemEval questions cannot be mixed in one arm.");
        }

        if (preparedQuestions == telemetry.Count && telemetry.Count != 0)
        {
            if (telemetry.Any(item =>
                    item.MessagesStored != 0 ||
                    item.MessagesPrepared <= 0 ||
                    item.ExtractionUnits != 0 ||
                    item.ExtractionUnitsPrepared <= 0 ||
                    // A retrieval that ran against a graph already PROVEN populated with complete
                    // provenance, and returned nothing, is a measured failure of retrieval - not an
                    // unsound preparation. Rejecting the run over it discards the very question that
                    // most sharply separates the arms.
                    (item.ItemsRetrieved == 0 &&
                     !IsScoredEmptyRetrieval(item.Status, item.GraphReadBack))))
            {
                issues.Add(
                    "At least one prepared LongMemEval question wrote during evaluation, lacks sealed preparation work, or retrieved no items.");
            }

            if (extractionCalls is not null && extractionCalls.Calls != 0)
            {
                issues.Add(
                    $"Observed {extractionCalls.Calls} extraction calls during prepared evaluation; expected zero.");
            }
        }
        else if (telemetry.Any(item =>
                     item.MessagesStored == 0 ||
                     (item.ItemsRetrieved == 0 &&
                      !IsScoredEmptyRetrieval(item.Status, item.GraphReadBack))))
        {
            issues.Add(
                "At least one LongMemEval question bypassed AgentMemory storage or retrieved no items.");
        }

        foreach (var failedStage in telemetry.Where(item =>
                     !string.Equals(item.Status, "completed", StringComparison.Ordinal) &&
                     !IsScoredEmptyRetrieval(item.Status, item.GraphReadBack)))
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
                continue;
            }

            if (!TryParseJudgeVerdict(explanation, out var judgedCorrect))
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
        if (!TryParseJudgeVerdict(explanation, out _))
            return "judge-invalid";
        return "completed";
    }

    internal static bool TryParseJudgeVerdict(string? explanation, out bool correct)
    {
        correct = false;
        if (string.IsNullOrWhiteSpace(explanation))
            return false;

        var value = explanation.Trim();
        if (TryReadLeadingVerdict(value, out correct))
            return true;

        // The judge does not always phrase the verdict the same way. Two prefixes were hardcoded -
        // "Judge said:" and "Judge outcome:" - and a third shape beginning "Judge" cost two of five
        // identical n=50 repeats, each rejecting a whole arm over one question. The diagnostic caught
        // it as FailureKind=unparseable, RejectedToken="Judge", and on one of those runs the retry
        // recovered the same question with a valid verdict: the judgement was fine, the parsing was
        // not.
        //
        // So: if the text opens with a short label ending in a colon, try again after it. The
        // tolerance is deliberately one-way - the prefix is only accepted when what follows is
        // ACTUALLY a yes or no, so this can never manufacture a verdict from a hedge like
        // "Judge verdict: partially correct". Bounded length, and only the first colon, so a
        // sentence that merely contains a colon cannot be mined for a verdict. The label itself must
        // begin "judg" (Judge / Judgement / Judgment / "Judge verdict"), which is what keeps
        // "maybe: yes" invalid - a guard test caught exactly that over-reach in the first attempt.
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon <= 32 &&
            value.AsSpan(0, colon).TrimStart().StartsWith("judg", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadLeadingVerdict(value[(colon + 1)..].Trim(), out correct);
        }

        return false;
    }

    /// <summary>Reads a verdict from the leading letter-token, or fails.</summary>
    private static bool TryReadLeadingVerdict(string value, out bool correct)
    {
        correct = false;
        var tokenLength = value.TakeWhile(char.IsLetter).Count();
        if (tokenLength == 0)
            return false;
        var token = value[..tokenLength];
        if (string.Equals(token, "yes", StringComparison.OrdinalIgnoreCase))
        {
            correct = true;
            return true;
        }

        return string.Equals(token, "no", StringComparison.OrdinalIgnoreCase);
    }
}
