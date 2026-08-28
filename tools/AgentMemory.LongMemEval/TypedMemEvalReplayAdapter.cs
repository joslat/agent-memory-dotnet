using AgentEval.Core;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Replays the answers a stored run already produced, so a judge can grade them again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> AgentEval disclosed that the Bitemporal vertical shipped with no judge
/// body: it fell through to the standard body, and the shared preamble's "premature" definition made
/// the judge grade the gold answer's JUSTIFICATION rather than its value. Every bitemporal number we
/// hold was produced by that judge. The model answers, however, were not — so the fix is to re-grade,
/// not to re-run.
/// </para>
/// <para>
/// <b>Why replaying through the REAL runner, rather than calling a judge directly.</b> A
/// hand-rolled grading loop would be a second implementation of scoring, free to drift from the one
/// that produces every other number we cite, and the drift would look like a finding. This adapter
/// instead satisfies <see cref="IEvaluableAgent"/> by handing back the stored answer, so the
/// question selection, the judge body, the typed-outcome derivation and the attribution accounting
/// are all AgentEval's own code paths, unchanged. Whatever the new package changes, this inherits.
/// </para>
/// <para>
/// <b>What makes it verifiable.</b> Replaying under the SAME judge that produced an artifact must
/// reproduce that artifact's verdicts. That check needs no new package and is the reason this can be
/// trusted before the fixed judge exists: an instrument that cannot reproduce a known result has no
/// business re-anchoring a baseline. Judges are LLMs, so the target is high agreement rather than
/// bit-identity, and the disagreements are inspected rather than averaged away.
/// </para>
/// <para>
/// <b>The history hooks are deliberately inert.</b> Nothing is retrieved here; the answer already
/// exists. Implementing them anyway keeps the adapter's shape identical to the real one, so the
/// runner takes the same branches — a replay that changed the runner's path would be measuring a
/// different harness.
/// </para>
/// </remarks>
internal sealed class TypedMemEvalReplayAdapter(
    IReadOnlyDictionary<string, string> answersByQuestion,
    string? modelId)
    : IEvaluableAgent, IHistoryInjectableAgent, ITimestampedHistoryInjectableAgent,
      ISessionResettableAgent
{
    private readonly List<string> _unmatched = [];

    public string Name => "AgentMemory.LongMemEval.Replay";

    /// <summary>Questions the runner asked that the stored artifact had no answer for.</summary>
    /// <remarks>
    /// A miss is not recoverable by guessing — an empty answer would be graded, and would score as a
    /// wrong answer rather than as a missing one, quietly lowering the re-graded baseline. Collected
    /// so the caller can fail the whole re-grade instead.
    /// </remarks>
    public IReadOnlyList<string> UnmatchedQuestions => _unmatched;

    public int Matched { get; private set; }

    public void InjectConversationHistory(
        IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns) { }

    public void InjectTimestampedConversationHistory(TimestampedConversationHistory history) { }

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        if (!answersByQuestion.TryGetValue(prompt, out var answer))
        {
            _unmatched.Add(prompt);
            // Still returns empty rather than throwing: one unmatched question should not abort a
            // 60-question re-grade, because the caller can only judge whether the mismatch is
            // systematic by seeing how many there are. UnmatchedQuestions is the gate.
            return Task.FromResult(new AgentResponse { Text = string.Empty, ModelId = modelId });
        }

        Matched++;
        return Task.FromResult(new AgentResponse { Text = answer, ModelId = modelId });
    }
}
