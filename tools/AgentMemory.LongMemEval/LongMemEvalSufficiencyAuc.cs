using System.Globalization;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Does the retrieval-sufficiency signal actually predict whether the answer was there?
/// </summary>
/// <remarks>
/// <para>
/// Per-section diagnostics now say <i>why</i> a section came back thin — searched and empty, searched
/// and short, top score, threshold. The abstention story downstream assumes those numbers <b>mean</b>
/// something: that a low top score or an empty section indicates the answer was not retrievable. That
/// assumption has never been tested, and if the scores are noise then every calibration, every
/// "I don't know" and every confidence surface built on them is decoration.
/// </para>
/// <para>
/// AUC is the right summary because it needs no threshold. Asking "does <c>topScore &lt; 0.7</c>
/// predict absence?" answers a question about 0.7; AUC asks whether the signal <b>orders</b> present
/// above absent at all, over every threshold at once. <b>0.5 is the kill line</b>: a signal that
/// orders no better than a coin has nothing to calibrate, and finding that out costs a few hours
/// rather than a quarter.
/// </para>
/// <para>
/// Computed by the rank formula (Mann–Whitney U), not by trapezoids over a swept threshold — ties are
/// the failure mode here, and they are not incidental. A signal that returns <c>0</c> for every empty
/// section produces a large tie block, and a curve-based implementation silently scores those as if
/// they were ordered. The rank form gives ties the 0.5 credit they have earned, so a degenerate signal
/// reports ≈0.5 instead of an accidental 0.8.
/// </para>
/// </remarks>
internal static class LongMemEvalSufficiencyAuc
{
    /// <summary>
    /// One question's pairing of the sufficiency signal with whether its answer was actually stored.
    /// </summary>
    /// <param name="Signal">
    /// Higher must mean "more sufficient". The natural signal is the section's top similarity score,
    /// with an empty section contributing its floor.
    /// </param>
    /// <param name="AnswerPresent">Whether the gold answer was found in stored memory at all.</param>
    internal readonly record struct Observation(double Signal, bool AnswerPresent);

    /// <summary>
    /// The AUC of <paramref name="observations"/>, plus the counts it was computed over.
    /// </summary>
    /// <remarks>
    /// Null AUC when either class is empty. That is not a degenerate 0.5 — it means the question was
    /// never asked, and reporting a number for it would be inventing evidence. A run where every
    /// answer was present, or none was, cannot say anything about ordering.
    /// </remarks>
    internal static LongMemEvalSufficiencyAucResult Compute(IReadOnlyList<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var present = observations.Where(o => o.AnswerPresent).ToArray();
        var absent = observations.Where(o => !o.AnswerPresent).ToArray();
        if (present.Length == 0 || absent.Length == 0)
            return new LongMemEvalSufficiencyAucResult(null, present.Length, absent.Length, 0);

        // Mann-Whitney U over mid-ranks. Every present/absent pair contributes 1 when the present one
        // scores higher, 0 when lower, and 0.5 when they tie -- which is what stops a signal that is
        // constant across most of the corpus from scoring well.
        var ranks = MidRanks([.. observations.Select(o => o.Signal)]);
        var presentRankSum = 0.0;
        var ties = 0;
        for (var i = 0; i < observations.Count; i++)
            if (observations[i].AnswerPresent) presentRankSum += ranks[i];

        var distinct = observations.Select(o => o.Signal).Distinct().Count();
        if (distinct < observations.Count) ties = observations.Count - distinct;

        var u = presentRankSum - (present.Length * (present.Length + 1) / 2.0);
        var auc = u / ((double)present.Length * absent.Length);
        return new LongMemEvalSufficiencyAucResult(auc, present.Length, absent.Length, ties);
    }

    private static double[] MidRanks(double[] values)
    {
        var order = Enumerable.Range(0, values.Length)
            .OrderBy(i => values[i])
            .ToArray();
        var ranks = new double[values.Length];
        var position = 0;
        while (position < order.Length)
        {
            var run = position + 1;
            while (run < order.Length && values[order[run]].Equals(values[order[position]])) run++;
            // 1-based mid-rank shared by the whole tie block.
            var mid = (position + run + 1) / 2.0;
            for (var i = position; i < run; i++) ranks[order[i]] = mid;
            position = run;
        }
        return ranks;
    }
}

/// <summary>The AUC, and everything needed to judge whether to believe it.</summary>
/// <param name="Auc">
/// Null when one class was empty — "not asked", never a default 0.5.
/// </param>
/// <param name="PresentCount">Questions whose gold answer was found in memory.</param>
/// <param name="AbsentCount">Questions whose gold answer was not.</param>
/// <param name="TiedObservations">
/// How many observations shared a signal value with another. A large figure means the signal is
/// mostly constant, and an AUC near 0.5 is then a property of the signal rather than of retrieval.
/// </param>
internal readonly record struct LongMemEvalSufficiencyAucResult(
    double? Auc, int PresentCount, int AbsentCount, int TiedObservations)
{
    /// <summary>
    /// Whether the signal orders better than a coin by a margin worth building on.
    /// </summary>
    /// <remarks>
    /// The threshold is stated here rather than chosen after seeing the number. 0.6 is deliberately
    /// unambitious: it is the level below which an abstention policy cannot beat "always answer" by
    /// enough to justify the refusals it would cause.
    /// </remarks>
    internal bool JustifiesAbstentionWork => Auc is >= 0.6;

    /// <summary>A one-line rendering that always states the denominators.</summary>
    /// <remarks>
    /// An AUC without its class counts is unreadable: 0.83 over 47 present and 3 absent is three
    /// questions' worth of evidence, and the bare number hides that completely.
    /// </remarks>
    internal string Describe() => Auc is not { } auc
        ? $"AUC not measured (present={PresentCount}, absent={AbsentCount}) - one class was empty, "
          + "so the signal was never asked to order anything"
        : string.Format(
            CultureInfo.InvariantCulture,
            "AUC {0:0.000} over {1} present / {2} absent ({3} tied){4}",
            auc, PresentCount, AbsentCount, TiedObservations,
            JustifiesAbstentionWork ? string.Empty : " - at or below the 0.6 line, abstention work is not justified");
}
