namespace AgentMemory.LongMemEval;

/// <summary>
/// What a single task asked for and what procedural memory returned (PLAN 7.7).
/// </summary>
/// <param name="TaskId">The task the agent was given.</param>
/// <param name="RetrievedProcedureIds">Procedures returned, best-ranked first. Empty means none.</param>
/// <param name="CorrectProcedureIds">
/// Procedures that genuinely apply to this task. Empty means <b>no</b> stored procedure applies, so
/// any retrieval is wrong however confident it looked.
/// </param>
public sealed record ProcedureRetrievalCase(
    string TaskId,
    IReadOnlyList<string> RetrievedProcedureIds,
    IReadOnlyList<string> CorrectProcedureIds);

/// <summary>
/// How often the retrieved procedure is the <i>right</i> one — not merely how fast it arrived
/// (PLAN 7.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The two failure modes are not equally bad, and a latency number cannot tell them apart.</b> An
/// agent with no procedural memory investigates: it is slower, and it is safe. An agent with the
/// <i>wrong</i> procedure executes — confidently, on a plan built for a different task. A promotion
/// feature that raises hit-rate while also raising the wrong-procedure rate can look like an
/// improvement in every efficiency measure it has.
/// </para>
/// <para>
/// So this reports three outcomes rather than an accuracy: correct, <b>wrong</b>, and abstained.
/// Abstention is deliberately not counted as a failure — it is the safe outcome, and folding it in
/// with wrong answers would make a cautious retriever look identical to a reckless one. That is the
/// same distinction Phase 5 drew for meta-memory, and it matters more here, because the cost of
/// acting on a wrong procedure is paid in tool calls rather than in a sentence.
/// </para>
/// <para>
/// Deterministic and provider-free: it scores id lists, so it can be asserted in a unit test rather
/// than measured against a model.
/// </para>
/// </remarks>
public sealed record ProcedureRetrievalPrecision(
    int Total,
    int CorrectAtOne,
    int WrongAtOne,
    int Abstained,
    int Missed,
    double MeanReciprocalRank)
{
    /// <summary>Share of tasks whose best-ranked procedure was a correct one.</summary>
    public double PrecisionAtOne => Total == 0 ? 0d : (double)CorrectAtOne / Total;

    /// <summary>
    /// Share of tasks where a procedure was returned and it was the wrong one — <b>the safety
    /// number</b>.
    /// </summary>
    /// <remarks>
    /// This is the figure a promotion change has to be judged against. Hit-rate and latency both
    /// improve when the retriever becomes more willing to answer; only this one gets worse.
    /// </remarks>
    public double WrongProcedureRate => Total == 0 ? 0d : (double)WrongAtOne / Total;

    /// <summary>
    /// Share of tasks where nothing was returned <b>and nothing applied</b> — the correct call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This used to count every empty retrieval, and that was wrong.</b> Returning nothing when a
    /// procedure DID apply is a miss, not caution — but it was scored here, in the column the
    /// documentation calls "not a failure". A retriever tuned to a threshold so high that it finds
    /// nothing would have scored a perfect wrong-procedure rate and a near-perfect abstention rate,
    /// i.e. maximally safe rather than useless.
    /// </para>
    /// <para>
    /// Found by building the first consumer for this instrument (26.2). It was invisible while the
    /// only caller was a unit test that supplied its own expectations.
    /// </para>
    /// </remarks>
    public double AbstentionRate => Total == 0 ? 0d : (double)Abstained / Total;

    /// <summary>
    /// Share of tasks where a procedure applied and nothing was returned — a failure, and a
    /// <i>safe</i> one.
    /// </summary>
    /// <remarks>
    /// Reported separately from both <see cref="WrongProcedureRate"/> and
    /// <see cref="AbstentionRate"/>, because it is neither. An agent that retrieves nothing
    /// investigates from scratch: it pays the discovery cost it should not have had to pay, but it
    /// does not act on a plan built for another task. Folding it into either neighbour loses exactly
    /// the distinction this instrument exists to preserve.
    /// </remarks>
    public double MissRate => Total == 0 ? 0d : (double)Missed / Total;

    /// <summary>
    /// Share of the tasks it <i>chose to answer</i> that it answered correctly.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="PrecisionAtOne"/> because the two move in opposite directions as a
    /// retriever gets more cautious, and reporting only one of them would make the trade invisible.
    /// </remarks>
    public double PrecisionWhenAnswering =>
        CorrectAtOne + WrongAtOne == 0 ? 0d : (double)CorrectAtOne / (CorrectAtOne + WrongAtOne);

    /// <summary>
    /// Scores a set of tasks. A case whose <see cref="ProcedureRetrievalCase.CorrectProcedureIds"/>
    /// is empty counts any retrieval as wrong: nothing stored applies, so answering is the error.
    /// </summary>
    public static ProcedureRetrievalPrecision Score(IReadOnlyList<ProcedureRetrievalCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);

        var correct = 0;
        var wrong = 0;
        var abstained = 0;
        var missed = 0;
        var reciprocalRankTotal = 0d;

        foreach (var item in cases)
        {
            if (item.RetrievedProcedureIds.Count == 0)
            {
                // Staying quiet is only the right call when nothing applied. Otherwise it is a miss:
                // safe, but a failure, and counted as one.
                if (item.CorrectProcedureIds.Count == 0) abstained++;
                else missed++;
                continue;
            }

            var expected = new HashSet<string>(item.CorrectProcedureIds, StringComparer.Ordinal);

            if (expected.Contains(item.RetrievedProcedureIds[0])) correct++;
            else wrong++;

            // Rank of the first correct procedure. Reported alongside precision@1 rather than instead
            // of it: an agent acts on the top result, so a correct procedure sitting at rank 3 is a
            // ranking signal, not a success.
            for (var rank = 0; rank < item.RetrievedProcedureIds.Count; rank++)
            {
                if (!expected.Contains(item.RetrievedProcedureIds[rank])) continue;
                reciprocalRankTotal += 1d / (rank + 1);
                break;
            }
        }

        return new ProcedureRetrievalPrecision(
            cases.Count,
            correct,
            wrong,
            abstained,
            missed,
            cases.Count == 0 ? 0d : reciprocalRankTotal / cases.Count);
    }
}
