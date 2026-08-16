using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// One question answered from perfect context by decomposition: split into sub-questions, answer each
/// against the same gold context, then compose the sub-answers into a final answer.
/// </summary>
public sealed record LongMemEvalDecomposedOracleResult(
    string QuestionId,
    string Status,
    IReadOnlyList<string> SubQuestions,
    IReadOnlyList<string> SubAnswers,
    string? ComposedAnswer,
    bool ValidVerdict,
    bool? Correct,
    double? RawScore,
    int LlmCalls,
    /// <summary>
    /// Calls spent on retried attempts, separate from the productive ones.
    /// </summary>
    /// <remarks>
    /// Kept apart so <see cref="LongMemEvalDecomposedOracle.ExpectedCalls"/> stays checkable: a run
    /// whose accounting cannot absorb a retry gets rejected by the validator for being flaky rather
    /// than for being wrong. Measured on the first probe: 1 of 2 questions hit a transient provider
    /// fault, and the same question succeeded immediately on re-run.
    /// </remarks>
    int RetriedCalls = 0);

/// <summary>
/// The decomposed arm of the oracle comparison.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this isolates.</b> Both arms answer from the <i>same</i> gold-session context, so retrieval
/// is held perfectly constant and the only variable is whether the question was decomposed. That
/// matters because 65 of 67 recorded wrong answers had the gold evidence already present — the loss
/// is at the answering stage, and no retrieval change can reach it.
/// </para>
/// <para>
/// <b>The composer deliberately does not see the context.</b> It receives the original question and
/// the sub-question/sub-answer pairs, nothing else. Handing it the transcript as well would make the
/// decomposed arm "the monolithic arm plus hints", and any win would be unattributable — it could
/// equally be the extra completion. Restricting the composer to sub-answers is what makes a positive
/// result mean <i>decomposition</i>.
/// </para>
/// <para>
/// <b>The decomposer is allowed to refuse.</b> A prompt that always splits would measure
/// split-everything rather than decomposition, and would make the witness in
/// <see cref="LongMemEvalOracleComparison"/> vacuous by construction. A question it judges atomic
/// comes back as a single sub-question and is recorded as undecomposed.
/// </para>
/// </remarks>
internal static class LongMemEvalDecomposedOracle
{
    /// <summary>Exact call count for a run that decomposed into <c>n</c> sub-questions.</summary>
    /// <remarks>
    /// Decompose (1) + one answer per sub-question (n) + compose (1) + judge (1). Published as a
    /// function rather than a constant because the harness's validator fail-closes on an exact call
    /// count — a run whose accounting disagrees with its behaviour is rejected, which has already
    /// cost this project one good run.
    /// </remarks>
    internal static int ExpectedCalls(int subQuestionCount) => subQuestionCount + 3;

    private const string DecomposePrompt =
        "You are preparing a question for a memory system. Break it into the smallest set of "
        + "self-contained sub-questions that must EACH be answered in order to answer the original.\n\n"
        + "Rules:\n"
        + "- If the question is already atomic, return it unchanged as the single line. Do NOT invent "
        + "sub-questions to appear thorough; an unnecessary split costs accuracy.\n"
        + "- Each sub-question must be answerable on its own, without reading the others.\n"
        + "- Do not answer anything. Do not number, explain, or add commentary.\n"
        + "- One sub-question per line, nothing else.";

    private const string ComposePrompt =
        "You are answering a question using ONLY the sub-answers supplied below. You do not have the "
        + "source material and must not guess beyond what the sub-answers state.\n\n"
        + "If the sub-answers are sufficient, combine them — including any arithmetic or comparison "
        + "the original question requires — and give the final answer directly.\n"
        + "If they are insufficient or contradict each other, say so plainly rather than choosing one.";

    /// <summary>
    /// One completion with bounded retry on a transient provider fault.
    /// </summary>
    /// <remarks>
    /// <b>Measured need, not caution.</b> The first two-question probe lost one question to a
    /// <c>ClientResultException</c> on its very first call, and the identical question succeeded on
    /// re-run — so without this a 30-question run bleeds rows to provider flakiness, inflating
    /// "inconclusive" and shrinking the comparable denominator that the whole comparison rests on.
    /// Every attempt is counted; retried ones are reported separately so the accounting still checks.
    /// </remarks>
    private static async Task<string> CompleteAsync(
        IChatClient chatClient,
        string systemPrompt,
        string userPrompt,
        int attempts,
        Counters counters,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                ], cancellationToken: cancellationToken).ConfigureAwait(false);
                counters.Calls++;
                if (attempt > 1) counters.Retried += attempt - 1;
                return response.Text ?? string.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (attempt < attempts)
            {
                // The failing call still cost the provider something even though it returned nothing,
                // so it is counted. Reporting only successful calls would understate the run's price.
                counters.Calls++;
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private sealed class Counters
    {
        public int Calls;
        public int Retried;
    }

    internal static async Task<LongMemEvalDecomposedOracleResult> RunAsync(
        IChatClient chatClient,
        LongMemEvalJudge judge,
        LongMemEvalEvidenceQuestion indexed,
        int maxSubQuestions,
        bool retainContent,
        CancellationToken cancellationToken,
        int attemptsPerCall = 3)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(indexed);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSubQuestions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsPerCall, 1);

        var counters = new Counters();
        var subQuestions = Array.Empty<string>();
        var subAnswers = new List<string>();

        try
        {
            // Identical to the monolithic arm's context, deliberately: gold sessions only, same time
            // signal. Any difference here would make the comparison measure two things at once.
            var goldContext = indexed.Messages
                .Where(message => indexed.AnswerSessionIds.Contains(message.SourceSessionId))
                .Select(message => (message.Role, message.SourceTimestamp, message.FormattedContent))
                .ToList();

            var decomposition = await CompleteAsync(
                chatClient, DecomposePrompt, indexed.Question, attemptsPerCall, counters,
                cancellationToken).ConfigureAwait(false);

            subQuestions = ParseSubQuestions(decomposition, indexed.Question, maxSubQuestions);

            foreach (var subQuestion in subQuestions)
            {
                var answerPrompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
                    goldContext, subQuestion, indexed.QuestionDate);
                subAnswers.Add(await CompleteAsync(
                    chatClient, AgentMemoryLongMemEvalAdapter.SystemPrompt, answerPrompt,
                    attemptsPerCall, counters, cancellationToken).ConfigureAwait(false));
            }

            var answer = await CompleteAsync(
                chatClient, ComposePrompt, BuildComposePrompt(indexed, subQuestions, subAnswers),
                attemptsPerCall, counters, cancellationToken).ConfigureAwait(false);

            var judgment = await judge.JudgeAsync(
                answer, ToBenchmarkQuestion(indexed), cancellationToken).ConfigureAwait(false);
            counters.Calls++;

            // Same validity rule as the monolithic arm. A verdict the parser and the judge disagree
            // about is unusable on BOTH arms or on neither -- applying a looser rule here would let
            // the decomposed arm bank verdicts the control could not.
            var valid = LongMemEvalRunValidator.TryParseJudgeVerdict(judgment.Explanation, out var parsed)
                && parsed == judgment.Correct;

            return new LongMemEvalDecomposedOracleResult(
                indexed.QuestionId,
                valid ? "completed" : "judge-invalid",
                subQuestions,
                retainContent ? subAnswers : [],
                retainContent ? answer : null,
                valid,
                valid ? judgment.Correct : null,
                valid ? judgment.RawScore : null,
                counters.Calls,
                counters.Retried);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Mirrors the monolithic oracle: a failure is recorded and excluded, never counted as a
            // wrong answer. Counting a provider fault against the arm would make an unreliable
            // deployment read as decomposition not working.
            return new LongMemEvalDecomposedOracleResult(
                indexed.QuestionId,
                $"threw:{ex.GetType().Name}",
                subQuestions,
                retainContent ? subAnswers : [],
                null,
                false,
                null,
                null,
                counters.Calls,
                counters.Retried);
        }
    }

    /// <summary>
    /// Reads the decomposer's reply into sub-questions, falling back to the original question.
    /// </summary>
    /// <remarks>
    /// The fallback is the safe direction: an unparseable decomposition yields the monolithic
    /// question, so the arm degrades into the control rather than answering something invented. It is
    /// recorded as one sub-question, which is exactly what the void witness is looking for.
    /// </remarks>
    internal static string[] ParseSubQuestions(string? reply, string original, int maxSubQuestions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSubQuestions, 1);

        if (string.IsNullOrWhiteSpace(reply)) return [original];

        var lines = reply
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Strip a leading enumerator the prompt asked for and models supply anyway.
            .Select(line => System.Text.RegularExpressions.Regex.Replace(
                line, @"^\s*(?:[-*•]|\d{1,2}[.)])\s+", string.Empty,
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length == 0) return [original];

        // Truncation is bounded rather than an error: cost per question must stay predictable, and a
        // decomposer that produced twelve sub-questions has misread the task rather than found twelve
        // genuine steps. The count is recorded, so an over-split is visible in the artifact.
        return lines.Length <= maxSubQuestions ? lines : lines[..maxSubQuestions];
    }

    private static string BuildComposePrompt(
        LongMemEvalEvidenceQuestion indexed,
        IReadOnlyList<string> subQuestions,
        IReadOnlyList<string> subAnswers)
    {
        var builder = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(indexed.QuestionDate))
            builder.Append("Current Date: ").AppendLine(indexed.QuestionDate).AppendLine();

        builder.AppendLine("Sub-answers:");
        for (var i = 0; i < subQuestions.Count; i++)
        {
            builder.Append("Q: ").AppendLine(subQuestions[i]);
            builder.Append("A: ").AppendLine(i < subAnswers.Count ? subAnswers[i] : "(no answer)");
            builder.AppendLine();
        }

        builder.AppendLine("Original question:");
        builder.AppendLine(indexed.Question);
        return builder.ToString();
    }

    private static ExternalBenchmarkQuestion ToBenchmarkQuestion(LongMemEvalEvidenceQuestion indexed) => new()
    {
        QuestionId = indexed.QuestionId,
        QuestionType = indexed.QuestionType,
        Question = indexed.Question,
        GoldAnswer = indexed.GoldAnswer,
        QuestionDate = indexed.QuestionDate,
        IsAbstention = indexed.IsAbstention,
    };
}
