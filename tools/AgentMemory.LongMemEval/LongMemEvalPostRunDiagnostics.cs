using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.LongMemEval;

internal enum LongMemEvalOracleMode
{
    None,
    Failed,
    All
}

public sealed record LongMemEvalJudgeRetryResult(
    string QuestionId,
    string Status,
    int Attempts,
    bool ValidVerdict,
    bool? Correct,
    double? RawScore,
    int LlmCalls,
    /// <summary>
    /// Why the verdict was not usable: <c>threw:&lt;ExceptionType&gt;</c> or <c>unparseable</c>.
    /// </summary>
    /// <remarks>
    /// The hybrid arm has been rejected three times on one question's judge verdict, and every time
    /// the reason was unknowable from the artifact: a bare catch made a provider failure look
    /// identical to a badly-shaped answer. Both are recorded now. Neither carries provider detail or
    /// user content — a type name and a single leading token are enough to tell the two apart.
    /// </remarks>
    string? FailureKind = null,
    /// <summary>
    /// The leading letter-token the parser rejected, which is the judge's own verdict word (e.g.
    /// "Partially"). Never the explanation body.
    /// </summary>
    string? RejectedToken = null);

public sealed record LongMemEvalOracleResult(
    string QuestionId,
    string Status,
    string? Answer,
    bool ValidVerdict,
    bool? Correct,
    double? RawScore,
    int LlmCalls);

public sealed record LongMemEvalFailureAttribution(
    string QuestionId,
    string Attribution,
    double? GoldSessionRecallAtK,
    bool? GoldTurnHitAtK,
    int? FirstGoldSessionRank,
    int? FirstGoldTurnRank);

public sealed record LongMemEvalPostRunDiagnosticsResult(
    int DiagnosticLlmCalls,
    IReadOnlyList<LongMemEvalJudgeRetryResult> JudgeRetries,
    IReadOnlyList<LongMemEvalOracleResult> OracleResults,
    IReadOnlyList<LongMemEvalFailureAttribution> Attributions);

/// <summary>
/// Runs diagnostics after AgentEval has produced the immutable benchmark result. Results from retries and
/// oracle evidence are reported separately and never rewrite the benchmark score or call count.
/// </summary>
internal static class LongMemEvalPostRunDiagnostics
{
    internal static async Task<LongMemEvalPostRunDiagnosticsResult> RunAsync(
        IChatClient chatClient,
        LongMemEvalEvidenceIndex evidenceIndex,
        IReadOnlyList<QuestionResult> questionResults,
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry,
        LongMemEvalOracleMode oracleMode,
        int judgeRetryAttempts,
        bool retainContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(evidenceIndex);
        ArgumentNullException.ThrowIfNull(questionResults);
        ArgumentNullException.ThrowIfNull(telemetry);
        if (judgeRetryAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(judgeRetryAttempts));

        var judge = new LongMemEvalJudge(
            chatClient,
            NullLogger<LongMemEvalJudge>.Instance);
        var retries = new List<LongMemEvalJudgeRetryResult>();
        var oracleResults = new List<LongMemEvalOracleResult>();
        var diagnosticCalls = 0;

        foreach (var question in questionResults.Where(NeedsJudgeRetry))
        {
            var indexed = evidenceIndex.GetByQuestionId(question.QuestionId);
            var retry = await RetryJudgeAsync(
                judge, indexed, question.AgentResponse, judgeRetryAttempts, cancellationToken)
                .ConfigureAwait(false);
            retries.Add(retry);
            diagnosticCalls += retry.LlmCalls;
        }

        foreach (var question in questionResults.Where(question =>
                     ShouldRunOracle(question, oracleMode)))
        {
            var indexed = evidenceIndex.GetByQuestionId(question.QuestionId);
            var oracle = await RunOracleAsync(
                chatClient, judge, indexed, retainContent, cancellationToken).ConfigureAwait(false);
            oracleResults.Add(oracle);
            diagnosticCalls += oracle.LlmCalls;
        }

        var retriesByQuestion = retries.ToDictionary(result => result.QuestionId, StringComparer.Ordinal);
        var oracleByQuestion = oracleResults.ToDictionary(result => result.QuestionId, StringComparer.Ordinal);
        var evidenceByQuestion = telemetry
            .Where(item => item.QuestionId is not null)
            .ToDictionary(item => item.QuestionId!, item => item.RetrievalEvidence, StringComparer.Ordinal);
        var coverageByQuestion = telemetry
            .Where(item => item.QuestionId is not null)
            .ToDictionary(item => item.QuestionId!, item => item.GoldEvidenceCoverage, StringComparer.Ordinal);
        var attributions = questionResults.Select(question =>
        {
            retriesByQuestion.TryGetValue(question.QuestionId, out var retry);
            oracleByQuestion.TryGetValue(question.QuestionId, out var oracle);
            evidenceByQuestion.TryGetValue(question.QuestionId, out var evidence);
            coverageByQuestion.TryGetValue(question.QuestionId, out var coverage);
            return new LongMemEvalFailureAttribution(
                question.QuestionId,
                Attribute(question, retry, oracle, evidence, coverage),
                evidence?.GoldSessionRecallAtK,
                evidence?.GoldTurnHitAtK,
                evidence?.FirstGoldSessionRank,
                evidence?.FirstGoldTurnRank);
        }).ToArray();

        return new LongMemEvalPostRunDiagnosticsResult(
            diagnosticCalls,
            retries.AsReadOnly(),
            oracleResults.AsReadOnly(),
            attributions);
    }

    /// <summary>
    /// G4-REF. The judge-retry pass alone, without oracle or gold-attribution. A reference arm has no
    /// retrieval, so running <see cref="Attribute"/> against it would label every failure
    /// <c>retrieval-evidence-missing</c> — inventing a retrieval cause for an arm that has no
    /// retrieval. Retries still matter, because BUG-J1's base-call accounting depends on them.
    /// </summary>
    internal static async Task<IReadOnlyList<LongMemEvalJudgeRetryResult>> RetryInvalidJudgeVerdictsAsync(
        IChatClient chatClient,
        LongMemEvalEvidenceIndex evidenceIndex,
        IReadOnlyList<QuestionResult> questionResults,
        int judgeRetryAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(evidenceIndex);
        ArgumentNullException.ThrowIfNull(questionResults);
        if (judgeRetryAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(judgeRetryAttempts));

        var judge = new LongMemEvalJudge(chatClient, NullLogger<LongMemEvalJudge>.Instance);
        var retries = new List<LongMemEvalJudgeRetryResult>();
        foreach (var question in questionResults.Where(NeedsJudgeRetry))
        {
            retries.Add(await RetryJudgeAsync(
                    judge,
                    evidenceIndex.GetByQuestionId(question.QuestionId),
                    question.AgentResponse,
                    judgeRetryAttempts,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return retries.AsReadOnly();
    }

    internal static string Attribute(
        QuestionResult question,
        LongMemEvalJudgeRetryResult? retry,
        LongMemEvalOracleResult? oracle,
        LongMemEvalRetrievalEvidence? evidence,
        LongMemEvalGoldEvidenceCoverage? goldCoverage = null)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!LongMemEvalRunValidator.TryParseJudgeVerdict(
                question.JudgeExplanation, out var baseVerdict))
        {
            if (retry is { ValidVerdict: true, Correct: true })
                return "judge-invalid-retry-correct";
            if (retry is { ValidVerdict: true, Correct: false })
                return "judge-invalid-retry-incorrect";
            return "judge-invalid";
        }

        if (question.Correct is true && baseVerdict)
            return "passed";
        if (question.Correct is null)
            return "judge-inconclusive";
        if (question.Correct.Value != baseVerdict)
            return "judge-result-mismatch";
        if (oracle is null)
            return "incorrect-needs-oracle";
        if (!oracle.ValidVerdict)
            return "oracle-inconclusive";
        if (oracle.Correct is not true)
            return "oracle-answer-or-benchmark-inconclusive";
        return ClassifyRetrievalEvidence(evidence, goldCoverage);
    }

    /// <summary>
    /// The evidence-dependent tail of <see cref="Classify"/>, reached only once judge and oracle
    /// states are resolved. Shared with <see cref="ClassifyForTest"/> so the two cannot drift.
    /// </summary>
    private static string ClassifyRetrievalEvidence(
        LongMemEvalRetrievalEvidence? evidence,
        LongMemEvalGoldEvidenceCoverage? goldCoverage = null)
    {
        // G3B.5: if the cold build learned nothing from the answer-bearing sessions, the question was
        // unanswerable before recall ever ran. Blaming retrieval - or calling it merely "not
        // observable" - would hide an extraction defect behind a retrieval label.
        if (goldCoverage is { EvidenceLearned: false })
            return "extraction-lost-evidence";
        if (evidence is null)
            return "retrieval-evidence-missing";
        // BUG-E1: gold attribution resolves only through recalled raw messages, so a mode with no
        // message budget (Structured) never gave retrieval a chance to hit. Blaming retrieval — or
        // falling through to an answer-synthesis verdict — would both be inventing a cause.
        if (!evidence.GoldAttributionObservable)
            return "retrieval-not-observable";
        if (evidence.GoldSessionRecallAtK is double sessionRecall && sessionRecall < 1d)
            return "retrieval-miss";
        if (evidence.GoldTurnHitAtK is false)
            return "retrieval-miss";
        return "answer-synthesis-failure";
    }

    /// <summary>Test seam for the gold-attribution branches.</summary>
    internal static string ClassifyForTest(
        LongMemEvalRetrievalEvidence? evidence,
        LongMemEvalGoldEvidenceCoverage? goldCoverage = null) =>
        ClassifyRetrievalEvidence(evidence, goldCoverage);

    private static bool NeedsJudgeRetry(QuestionResult question) =>
        !IsAgentFailure(question) &&
        !LongMemEvalRunValidator.TryParseJudgeVerdict(question.JudgeExplanation, out _);

    private static bool ShouldRunOracle(
        QuestionResult question,
        LongMemEvalOracleMode oracleMode) =>
        !IsAgentFailure(question) && oracleMode switch
        {
            LongMemEvalOracleMode.All => true,
            LongMemEvalOracleMode.Failed => question.Correct is not true ||
                !LongMemEvalRunValidator.TryParseJudgeVerdict(question.JudgeExplanation, out _),
            _ => false
        };

    private static bool IsAgentFailure(QuestionResult question)
    {
        var response = question.AgentResponse ?? string.Empty;
        return response.StartsWith("[ERROR:", StringComparison.OrdinalIgnoreCase) ||
               response.StartsWith("[CONTENT_FILTER]", StringComparison.OrdinalIgnoreCase) ||
               (question.JudgeExplanation ?? string.Empty).StartsWith(
                   "Skipped due to error:", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<LongMemEvalJudgeRetryResult> RetryJudgeAsync(
        LongMemEvalJudge judge,
        LongMemEvalEvidenceQuestion indexed,
        string agentResponse,
        int attempts,
        CancellationToken cancellationToken)
    {
        if (attempts == 0)
        {
            return new LongMemEvalJudgeRetryResult(
                indexed.QuestionId, "disabled", 0, false, null, null, 0);
        }

        string? failureKind = null;
        string? rejectedToken = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var judgment = await judge.JudgeAsync(
                    agentResponse,
                    Question(indexed),
                    cancellationToken).ConfigureAwait(false);
                if (!LongMemEvalRunValidator.TryParseJudgeVerdict(
                        judgment.Explanation, out var parsed))
                {
                    rejectedToken = LeadingToken(judgment.Explanation);
                    failureKind = "unparseable";
                }
                else if (parsed != judgment.Correct)
                {
                    failureKind = "verdict-disagrees-with-score";
                }

                if (LongMemEvalRunValidator.TryParseJudgeVerdict(
                        judgment.Explanation, out parsed) &&
                    parsed == judgment.Correct)
                {
                    return new LongMemEvalJudgeRetryResult(
                        indexed.QuestionId,
                        "recovered",
                        attempt,
                        true,
                        judgment.Correct,
                        judgment.RawScore,
                        attempt);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Provider details are intentionally excluded from the durable artifact; the type
                // name is not a provider detail and is the difference between "the judge refused"
                // and "the judge answered in a shape we do not parse".
                failureKind = "threw:" + exception.GetType().Name;
            }
        }

        return new LongMemEvalJudgeRetryResult(
            indexed.QuestionId, "invalid", attempts, false, null, null, attempts,
            failureKind ?? "unparseable",
            rejectedToken);
    }


    /// <summary>The leading letter-token of a judge explanation, capped, for diagnostics only.</summary>
    private static string LeadingToken(string? explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation))
            return "<empty>";
        var trimmed = explanation.TrimStart();
        var token = new string(trimmed.TakeWhile(char.IsLetter).ToArray());
        return token.Length == 0 ? "<non-letter>" : token[..Math.Min(token.Length, 24)];
    }

    private static async Task<LongMemEvalOracleResult> RunOracleAsync(
        IChatClient chatClient,
        LongMemEvalJudge judge,
        LongMemEvalEvidenceQuestion indexed,
        bool retainContent,
        CancellationToken cancellationToken)
    {
        var calls = 0;
        try
        {
            // G3B.2: the oracle gets the same time signal as every other arm, or "perfect retrieval"
            // would be measured against a strictly worse prompt than the thing it bounds.
            var answerPrompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
                indexed.Messages
                    .Where(message => indexed.AnswerSessionIds.Contains(message.SourceSessionId))
                    .Select(message => (message.Role, message.SourceTimestamp, message.FormattedContent)),
                indexed.InvocationPrompt,
                indexed.QuestionDate);
            var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, AgentMemoryLongMemEvalAdapter.SystemPrompt),
                new ChatMessage(ChatRole.User, answerPrompt)
            ], cancellationToken: cancellationToken).ConfigureAwait(false);
            calls++;
            var answer = response.Text ?? string.Empty;
            var judgment = await judge.JudgeAsync(
                answer, Question(indexed), cancellationToken).ConfigureAwait(false);
            calls++;
            var valid = LongMemEvalRunValidator.TryParseJudgeVerdict(
                judgment.Explanation, out var parsed) &&
                parsed == judgment.Correct;
            return new LongMemEvalOracleResult(
                indexed.QuestionId,
                valid ? "completed" : "judge-invalid",
                retainContent ? answer : null,
                valid,
                valid ? judgment.Correct : null,
                valid ? judgment.RawScore : null,
                calls);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new LongMemEvalOracleResult(
                indexed.QuestionId, "error", null, false, null, null, calls);
        }
    }

    private static ExternalBenchmarkQuestion Question(LongMemEvalEvidenceQuestion indexed) => new()
    {
        QuestionId = indexed.QuestionId,
        QuestionType = indexed.QuestionType,
        Question = indexed.Question,
        GoldAnswer = indexed.GoldAnswer,
        QuestionDate = indexed.QuestionDate,
        IsAbstention = indexed.IsAbstention
    };
}