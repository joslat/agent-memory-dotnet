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
    int LlmCalls);

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
        var attributions = questionResults.Select(question =>
        {
            retriesByQuestion.TryGetValue(question.QuestionId, out var retry);
            oracleByQuestion.TryGetValue(question.QuestionId, out var oracle);
            evidenceByQuestion.TryGetValue(question.QuestionId, out var evidence);
            return new LongMemEvalFailureAttribution(
                question.QuestionId,
                Attribute(question, retry, oracle, evidence),
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

    internal static string Attribute(
        QuestionResult question,
        LongMemEvalJudgeRetryResult? retry,
        LongMemEvalOracleResult? oracle,
        LongMemEvalRetrievalEvidence? evidence)
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
        if (evidence is null)
            return "retrieval-evidence-missing";
        if (evidence.GoldSessionRecallAtK is double sessionRecall && sessionRecall < 1d)
            return "retrieval-miss";
        if (evidence.GoldTurnHitAtK is false)
            return "retrieval-miss";
        return "answer-synthesis-failure";
    }

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

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var judgment = await judge.JudgeAsync(
                    agentResponse,
                    Question(indexed),
                    cancellationToken).ConfigureAwait(false);
                if (LongMemEvalRunValidator.TryParseJudgeVerdict(
                        judgment.Explanation, out var parsed) &&
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
            catch
            {
                // Provider details are intentionally excluded from the durable artifact.
            }
        }

        return new LongMemEvalJudgeRetryResult(
            indexed.QuestionId, "invalid", attempts, false, null, null, attempts);
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
            var answerPrompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
                indexed.Messages
                    .Where(message => indexed.AnswerSessionIds.Contains(message.SourceSessionId))
                    .Select(message => (message.Role, message.FormattedContent)),
                indexed.InvocationPrompt);
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