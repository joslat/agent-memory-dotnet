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
    string? RejectedToken = null,
    /// <summary>
    /// The leading letter-token <b>after</b> a <c>Judge:</c>-style label, when one is present.
    /// </summary>
    /// <remarks>
    /// <see cref="RejectedToken"/> alone cannot tell the two failures apart. Both of these report
    /// <c>RejectedToken = "Judge"</c>:
    /// <list type="bullet">
    /// <item><description><c>"Judge: yes, the answer is correct"</c> — a <b>parser defect</b>, the
    /// verdict is right there.</description></item>
    /// <item><description><c>"Judge deemed the response partially correct"</c> — a <b>genuine
    /// non-verdict</b>, and refusing it is correct behaviour.</description></item>
    /// </list>
    /// Question <c>7405e8b1</c> failed this way in two separate runs and the artifact could not say
    /// which case it was, so the same investigation had to start from nothing twice. One extra token
    /// settles it, and carries no more user content than the existing one does.
    /// </remarks>
    string? RejectedAfterLabel = null);

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
        CancellationToken cancellationToken = default,
        JudgeVerdictProtocol verdictProtocol = JudgeVerdictProtocol.FreeText)
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

        // 3.7. NeedsJudgeRetry is a free-text parse check, so under StructuredJson it reports every
        // question as needing repair and the retry fires N times -- which is what zeroed the base
        // judge-call count and tripped the bound. Only the RETRY is suppressed; the oracle pass below
        // is a separate loop and still runs, so no diagnostic coverage is lost.
        foreach (var question in questionResults.Where(q =>
                     verdictProtocol != JudgeVerdictProtocol.StructuredJson && NeedsJudgeRetry(q)))
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
        CancellationToken cancellationToken = default,
        JudgeVerdictProtocol verdictProtocol = JudgeVerdictProtocol.FreeText)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(evidenceIndex);
        ArgumentNullException.ThrowIfNull(questionResults);
        if (judgeRetryAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(judgeRetryAttempts));

        // 3.7. The retry repairs an UNPARSEABLE FREE-TEXT verdict. Under StructuredJson there is no
        // prose to re-parse, so it would fire on every question -- which is precisely what drove base
        // judge calls to zero and tripped the call-accounting bound on the first StructuredJson run.
        // Suppressing it here is what lets that bound pass untouched rather than being widened.
        if (verdictProtocol == JudgeVerdictProtocol.StructuredJson)
            return Array.Empty<LongMemEvalJudgeRetryResult>();

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
        string? rejectedAfterLabel = null;
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
                    rejectedAfterLabel = LeadingTokenAfterLabel(judgment.Explanation);
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
            rejectedToken,
            rejectedAfterLabel);
    }


    /// <summary>The leading letter-token of a judge explanation, capped, for diagnostics only.</summary>
    /// <summary>
    /// The leading letter-token after a short <c>Judge:</c>-style label, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Mirrors the label rule in <c>LongMemEvalRunValidator.TryParseJudgeVerdict</c> exactly — same
    /// 32-character bound, same <c>"judg"</c> prefix — so the diagnostic describes the parser that
    /// actually ran rather than a second, drifting copy of its logic.
    /// </remarks>
    private static string? LeadingTokenAfterLabel(string? explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation))
            return null;

        var value = explanation.Trim();
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon > 32 ||
            !value.AsSpan(0, colon).TrimStart().StartsWith("judg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return LeadingToken(value[(colon + 1)..].Trim());
    }

    private static string LeadingToken(string? explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation))
            return "<empty>";
        var trimmed = explanation.TrimStart();
        var token = new string(trimmed.TakeWhile(char.IsLetter).ToArray());
        return token.Length == 0 ? "<non-letter>" : token[..Math.Min(token.Length, 24)];
    }

    /// <remarks>
    /// Internal rather than private so the decomposed-oracle comparison uses <b>this</b> code as its
    /// control. A reimplemented monolithic arm would drift from the one every archived attribution was
    /// produced by, and the comparison would then be measuring two differences at once.
    /// </remarks>
    internal static async Task<LongMemEvalOracleResult> RunOracleAsync(
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