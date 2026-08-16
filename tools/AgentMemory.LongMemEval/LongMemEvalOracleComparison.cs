namespace AgentMemory.LongMemEval;

/// <summary>
/// One question, answered twice from the <b>same</b> perfect context: once monolithically, once
/// decomposed into sub-questions whose answers are then composed.
/// </summary>
/// <param name="QuestionId">The dataset question.</param>
/// <param name="MonolithicCorrect">The existing oracle's verdict, or null when it was inconclusive.</param>
/// <param name="DecomposedCorrect">The decomposed arm's verdict, or null when it was inconclusive.</param>
/// <param name="SubQuestionCount">
/// How many sub-questions the decomposer actually produced. <b>1 means nothing was decomposed</b> —
/// the arm ran as the monolithic arm with extra steps.
/// </param>
public sealed record LongMemEvalOraclePair(
    string QuestionId,
    bool? MonolithicCorrect,
    bool? DecomposedCorrect,
    int SubQuestionCount);

/// <summary>
/// The result of the decomposed-vs-monolithic oracle comparison.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this experiment exists.</b> Across 62 recorded reports, <b>65 of 67</b> wrong answers had
/// the gold evidence already retrieved or present — 97%. Our failures are overwhelmingly not
/// retrieval failures, so a change to retrieval has a ceiling of about 3% of the loss. This isolates
/// the other stage: both arms answer from the <i>same</i> gold-session context, so retrieval is held
/// perfectly constant and the only variable is whether the question was decomposed.
/// </para>
/// <para>
/// <b>It is deliberately an upper bound, not a product measurement.</b> Perfect context is not what a
/// deployed system has. If decomposition cannot beat the monolithic arm here, it will not beat it on
/// real retrieval either — which is the cheap kill this comparison exists to make available before
/// any production code is written.
/// </para>
/// </remarks>
public sealed record LongMemEvalOracleComparison
{
    /// <summary>Every paired observation, in question order.</summary>
    public required IReadOnlyList<LongMemEvalOraclePair> Pairs { get; init; }

    /// <summary>Pairs where both arms returned a usable verdict. The comparison's real denominator.</summary>
    public int Comparable { get; init; }

    /// <summary>Both arms correct.</summary>
    public int BothCorrect { get; init; }

    /// <summary>
    /// Both arms wrong. <b>Not evidence against decomposition</b> — this bucket holds the
    /// oracle-impossible questions, which no answering strategy can reach.
    /// </summary>
    public int BothWrong { get; init; }

    /// <summary>Decomposed correct, monolithic wrong — the discordant pair that favours decomposition.</summary>
    public int DecomposedOnly { get; init; }

    /// <summary>Monolithic correct, decomposed wrong — the discordant pair that counts against it.</summary>
    public int MonolithicOnly { get; init; }

    /// <summary>
    /// Pairs excluded because at least one arm's judge verdict was unusable.
    /// </summary>
    /// <remarks>
    /// Reported rather than folded into "wrong". An inconclusive verdict is not a failure of the arm,
    /// and counting it as one would make a judge that struggles with decomposed answers look like
    /// decomposition failing.
    /// </remarks>
    public int Inconclusive { get; init; }

    /// <summary>
    /// How many comparable questions the decomposer actually split into two or more sub-questions.
    /// </summary>
    /// <remarks>
    /// <b>The witness.</b> A decomposer that returns the original question unchanged produces an arm
    /// identical to the control, and the comparison then reports "no difference" — a result about the
    /// decomposer having never run, wearing the authority of a controlled experiment. This project
    /// has voided six measurement runs to exactly that shape. <see cref="IsVoid"/> refuses it.
    /// </remarks>
    public int ActuallyDecomposed { get; init; }

    /// <summary>
    /// True when the run cannot support any conclusion and must not be reported as one.
    /// </summary>
    /// <remarks>
    /// Void when nothing was decomposed (the arms are the same arm) or when nothing was comparable
    /// (no question produced two usable verdicts). Both cases yield a difference of zero that says
    /// nothing about decomposition.
    /// </remarks>
    public bool IsVoid => ActuallyDecomposed == 0 || Comparable == 0;

    /// <summary>
    /// The two discordant counts, which are the whole of the evidence.
    /// </summary>
    /// <remarks>
    /// Concordant pairs carry no information about a difference between the arms — McNemar's test
    /// uses only the discordant ones. Reporting an accuracy difference instead would let a large
    /// agreeing majority dilute the signal in both directions.
    /// </remarks>
    public (int Favouring, int Against) Discordant => (DecomposedOnly, MonolithicOnly);

    /// <summary>
    /// Builds the comparison. Pure arithmetic over paired verdicts; no provider involvement.
    /// </summary>
    public static LongMemEvalOracleComparison From(IEnumerable<LongMemEvalOraclePair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var materialised = pairs.ToList();

        int both = 0, neither = 0, dec = 0, mono = 0, inconclusive = 0, decomposed = 0;
        foreach (var pair in materialised)
        {
            if (pair.MonolithicCorrect is not { } m || pair.DecomposedCorrect is not { } d)
            {
                inconclusive++;
                continue;
            }

            // Counted over COMPARABLE pairs only. A question the decomposer split but whose verdict
            // was unusable proves the decomposer ran, but contributes no evidence -- and letting it
            // satisfy the witness would license a conclusion drawn from zero usable observations.
            if (pair.SubQuestionCount >= 2) decomposed++;

            if (m && d) both++;
            else if (!m && !d) neither++;
            else if (d) dec++;
            else mono++;
        }

        return new LongMemEvalOracleComparison
        {
            Pairs = materialised,
            Comparable = both + neither + dec + mono,
            BothCorrect = both,
            BothWrong = neither,
            DecomposedOnly = dec,
            MonolithicOnly = mono,
            Inconclusive = inconclusive,
            ActuallyDecomposed = decomposed,
        };
    }

    /// <summary>
    /// A one-line summary safe to print, carrying the denominators rather than a bare percentage.
    /// </summary>
    public string Describe() => IsVoid
        ? $"VOID — comparable {Comparable}, actually decomposed {ActuallyDecomposed}. "
          + "A difference of zero here is a statement about the decomposer, not about decomposition."
        : $"comparable {Comparable} · both correct {BothCorrect} · both wrong {BothWrong} · "
          + $"decomposed-only {DecomposedOnly} · monolithic-only {MonolithicOnly} · "
          + $"decomposed {ActuallyDecomposed}/{Comparable} · inconclusive {Inconclusive}";
}
