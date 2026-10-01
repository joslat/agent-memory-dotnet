using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The judge's own record of each question, as every report writes it.
/// </summary>
/// <remarks>
/// <para>
/// One projection for the prepared pair and the reference arms. The prepared pair kept the agent's
/// answer, the judge's explanation and the typed status; the reference arms kept only the verdict, so
/// their answers could never be re-judged or audited afterwards. Two copies of the same shape were how
/// they drifted apart in the first place.
/// </para>
/// <para>
/// Emitted under the evidence-detail setting, so a run that must not retain answer text still can't.
/// </para>
/// </remarks>
internal static class LongMemEvalJudgmentProjection
{
    internal static object[]? Project(
        IEnumerable<QuestionResult> questionResults,
        LongMemEvalEvidenceDetail evidenceDetail)
    {
        ArgumentNullException.ThrowIfNull(questionResults);
        return evidenceDetail == LongMemEvalEvidenceDetail.None
            ? null
            : questionResults.Select(q => (object)new
            {
                q.QuestionId,
                status = q.JudgeStatus?.ToString(),
                q.Correct,
                q.RawScore,
                q.JudgeLlmCallCount,
                // Separated at the question level too: JudgeLlmCallCount mixes primary and retry
                // calls, which is what made a run's accounting unauditable after the fact.
                q.JudgeRetryLlmCallCount,
                q.JudgeTokensUsed,
                agentResponse = q.AgentResponse,
                judgeExplanation = q.JudgeExplanation,
            }).ToArray();
    }
}
