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
/// <b>Keyed by POSITION, not by question text, and that is a corpus fact rather than a preference.</b>
/// Twelve of the sixty bitemporal questions share their text with another question and carry a
/// DIFFERENT gold answer -- <c>tme-bit-007</c> and <c>tme-bit-037</c> ask "which department was Colm
/// Whitaker at in February?" and answer Lowick and Marchmont respectively. Each question is asked
/// against its own injected history, so identical words over different memory legitimately have
/// different right answers. A text-keyed replay would therefore have handed 12 of 60 questions an
/// answer written for a different question and graded it, which is not a lost row but a WRONG one.
/// </para>
/// <para>
/// <b>The position assumption is asserted, not trusted.</b> <see cref="InvokeAsync"/> receives only
/// the prompt, so ordering is the only identity available; it is verified by checking that the text
/// at each position still matches the stored row. If the runner ever selects or orders questions
/// differently, that check fires instead of silently pairing the wrong answers -- the failure mode
/// this whole class exists to make impossible.
/// </para>
/// <para>
/// <b>The history hooks are deliberately inert.</b> Nothing is retrieved here; the answer already
/// exists. Implementing them anyway keeps the adapter's shape identical to the real one, so the
/// runner takes the same branches — a replay that changed the runner's path would be measuring a
/// different harness.
/// </para>
/// </remarks>
internal sealed class TypedMemEvalReplayAdapter(
    IReadOnlyList<(string Question, string Answer)> storedRows,
    string? modelId)
    : IEvaluableAgent, IHistoryInjectableAgent, ITimestampedHistoryInjectableAgent,
      ISessionResettableAgent
{
    private readonly List<string> _unmatched = [];
    private readonly List<string> _orderingMismatches = [];
    private int _next;

    public string Name => "AgentMemory.LongMemEval.Replay";

    /// <summary>Questions the runner asked that the stored artifact had no answer for.</summary>
    /// <remarks>
    /// A miss is not recoverable by guessing — an empty answer would be graded, and would score as a
    /// wrong answer rather than as a missing one, quietly lowering the re-graded baseline. Collected
    /// so the caller can fail the whole re-grade instead.
    /// </remarks>
    // Snapshot, not the backing list: a caller that downcast and mutated this would corrupt
    // the accounting the whole re-grade gate depends on.
    public IReadOnlyList<string> UnmatchedQuestions => _unmatched.ToArray();

    /// <summary>
    /// Positions where the runner's question did not match the stored row at that position.
    /// </summary>
    /// <remarks>
    /// Non-empty means the replay is pairing answers with the wrong questions, which produces a
    /// fully-populated, entirely meaningless score. Fatal to the caller, never a warning.
    /// </remarks>
    public IReadOnlyList<string> OrderingMismatches => _orderingMismatches.ToArray();

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

        if (_next >= storedRows.Count)
        {
            // The runner asked more questions than the artifact holds answers for. Recorded rather
            // than thrown so the caller sees the full extent before aborting.
            _unmatched.Add(prompt);
            return Task.FromResult(new AgentResponse { Text = string.Empty, ModelId = modelId });
        }

        var row = storedRows[_next++];
        if (!string.Equals(row.Question, prompt, StringComparison.Ordinal))
        {
            _orderingMismatches.Add(prompt);
            return Task.FromResult(new AgentResponse { Text = string.Empty, ModelId = modelId });
        }

        Matched++;
        return Task.FromResult(new AgentResponse { Text = row.Answer, ModelId = modelId });
    }
}
