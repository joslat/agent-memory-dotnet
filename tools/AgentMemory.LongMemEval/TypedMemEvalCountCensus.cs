using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The counting census: how a counting answer was wrong, not merely that it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the decision statistic for the identity work, and it was computed by hand.</b> The
/// figures the E-1 preregs rest on — "1 exact, 14 undercounts, 0 overcounts, 15 of 43 gold
/// recovered" — appear in correspondence and in code comments, and nowhere in the tool. A statistic
/// that a person derives by reading a report is not reproducible, cannot be red-probed, and its
/// guard is a promise rather than a check.
/// </para>
/// <para>
/// <b>Why the direction is the whole point.</b> A score says a counting question was missed.
/// Undercounting means evidence did not arrive; overcounting means evidence arrived that should not
/// have. Those are opposite defects with opposite fixes, and one of them — overcounts appearing — is
/// the registered Goodhart signature for identity capture: if counts rise past gold, distinct
/// referents have been merged and the score improves BECAUSE the store became wronger. Fourteen
/// undercounts with zero overcounts is a one-directional loss, which is the signature of a missing
/// join rather than a bad ranking, and it is the entire argument for E-1.
/// </para>
/// <para>
/// <b>Unparseable is its own outcome.</b> An answer with no number in it has not undercounted — it
/// has declined to count, which is a different thing and must not be folded into either direction.
/// One AFTER answer in the first entity-linking arm was unparseable, and folding it in was what made
/// that arm's gold denominators disagree (45 vs 42).
/// </para>
/// </remarks>
internal static partial class TypedMemEvalCountCensus
{
    /// <summary>
    /// Whether a shape's answers are COUNTS OF ITEMS, and so inside this census.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A named suffix, not a heuristic, and the restriction is load-bearing.</b> Run unrestricted,
    /// this census reports <c>arithmetic-sum</c> and <c>arithmetic-delta</c> as carrying seven and
    /// eight "overcounts" — but their golds are magnitudes (3,297; 3,012), and answering 3,400 is an
    /// arithmetic error, not two referents merged into one. Firing the over-merge guard there is a
    /// category error, and a guard that cries wolf on every arithmetic run teaches its reader to skip
    /// the line. This codebase already learned that elsewhere: a warning printed where a zero is the
    /// correct and expected state is how a gate stops being read at all.
    /// </para>
    /// <para>
    /// The over-merge signature is specific: identity capture that merges distinct referents makes a
    /// COUNT OF THINGS rise past the number of things there are. That is only meaningful where the
    /// answer counts items — <c>alias-then-count</c>, <c>value-then-count</c>, <c>arithmetic-count</c>
    /// — so membership is decided by the shape's own name and nothing else.
    /// </para>
    /// </remarks>
    internal static bool IsCountingShape(string? questionType) =>
        questionType is { Length: > 0 }
        && questionType.EndsWith("count", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first integer in a counting answer, or null when it carries none.
    /// </summary>
    /// <remarks>
    /// <b>First, not largest or last.</b> These answers lead with their count — "2 deliveries were
    /// taken at head office (the Calderwick office)" — and the trailing parenthetical routinely
    /// contains other numbers. Taking the largest would read the wrong one precisely on the answers
    /// that name a resolved alias, which are the answers this census exists to examine.
    /// </remarks>
    internal static int? ParseCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = LeadingInteger().Match(text);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var value))
        {
            return value;
        }

        // AN ASSERTED ZERO IS A COUNT, NOT A REFUSAL, and the difference is not pedantic: "no
        // deliveries at the annexe are recorded" answers the question with zero and undercounts by
        // the whole gold, while "I don't have any retrieved record" declines to answer it. Both
        // contain no digit, and reading both as silence loses a real wrong answer -- which is what
        // made the hand-derived baseline disagree with this one.
        //
        // The line is WHAT THE SENTENCE IS ABOUT: a claim about the world or the record is an
        // answer; a claim about the assistant's own reach is not.
        return DeclinesToAnswer().IsMatch(text) ? null
            : AssertsNone().IsMatch(text) ? 0
            : null;
    }

    /// <summary>How one counting answer stands against its gold.</summary>
    internal static CountOutcome Classify(string? goldAnswer, string? response)
    {
        var gold = ParseCount(goldAnswer);
        var given = ParseCount(response);

        // No gold count is not a verdict about the answer: the question was not a counting question,
        // or its gold is phrased in a way this census cannot read. Either way it is excluded rather
        // than scored, because a denominator that quietly absorbs unreadable rows is how a census
        // starts reporting a population it never measured.
        if (gold is not { } expected) return CountOutcome.NotCountable;
        if (given is not { } actual) return CountOutcome.Unparseable;

        return actual == expected ? CountOutcome.Exact
            : actual < expected ? CountOutcome.Undercount
            : CountOutcome.Overcount;
    }

    /// <summary>Runs the census over a set of answered questions.</summary>
    internal static CountCensus Measure(IEnumerable<(string? Gold, string? Response)> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        int exact = 0, under = 0, over = 0, unparseable = 0, notCountable = 0;
        int goldTotal = 0, recovered = 0;

        foreach (var (gold, response) in answers)
        {
            var outcome = Classify(gold, response);
            switch (outcome)
            {
                case CountOutcome.Exact: exact++; break;
                case CountOutcome.Undercount: under++; break;
                case CountOutcome.Overcount: over++; break;
                case CountOutcome.Unparseable: unparseable++; break;
                default: notCountable++; continue;
            }

            if (ParseCount(gold) is not { } expected) continue;
            goldTotal += expected;

            // CAPPED AT GOLD, deliberately. Recovery asks how much of the gold was found; an
            // overcount has not found MORE gold than exists, it has added something that is not
            // gold. Letting an overcount raise recovery would let the one failure this census exists
            // to catch improve the number it is judged by.
            if (ParseCount(response) is { } actual) recovered += Math.Min(actual, expected);
        }

        return new CountCensus(exact, under, over, unparseable, notCountable, goldTotal, recovered);
    }

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex LeadingInteger();

    /// <summary>Phrasings that assert an empty result — an answer of zero.</summary>
    [GeneratedRegex(@"\b(no|none|zero)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssertsNone();

    /// <summary>
    /// Phrasings about the ASSISTANT'S reach rather than about the world. Checked FIRST, because
    /// "I don't have any record of no deliveries" would otherwise read as an asserted zero.
    /// </summary>
    [GeneratedRegex(
        @"(i (do not|don't) have|i (cannot|can't|could not|couldn't) (find|determine|tell)|"
        + @"(have )?no retrieved|not enough information|unable to)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeclinesToAnswer();
}

/// <summary>How a counting answer stood against its gold.</summary>
internal enum CountOutcome
{
    /// <summary>The gold is not a count, so this question is outside the census.</summary>
    NotCountable,

    /// <summary>The answer carries no number at all — it declined to count.</summary>
    Unparseable,

    /// <summary>The count matched.</summary>
    Exact,

    /// <summary>Fewer than gold: evidence did not arrive. The signature of a missing join.</summary>
    Undercount,

    /// <summary>More than gold: evidence arrived that should not have. The over-merge signature.</summary>
    Overcount,
}

/// <summary>The census over a set of counting answers.</summary>
/// <param name="Exact">Counts that matched gold.</param>
/// <param name="Undercounts">Counts below gold — evidence missing.</param>
/// <param name="Overcounts">
/// Counts above gold. <b>The registered Goodhart signature.</b> Identity capture that merges
/// distinct referents inflates counts, and the score improves because the store became wronger.
/// </param>
/// <param name="Unparseable">Answers carrying no number. Not an undercount; a refusal to count.</param>
/// <param name="NotCountable">Questions whose gold is not a count. Excluded, not scored.</param>
/// <param name="GoldTotal">Total countable items across the gold answers.</param>
/// <param name="Recovered">
/// Gold items found, capped per question at that question's gold. The finest grain available —
/// 43 items rather than 15 questions — which is why it is the primary quantity when a shape is too
/// small for its score to move outside noise.
/// </param>
internal readonly record struct CountCensus(
    int Exact,
    int Undercounts,
    int Overcounts,
    int Unparseable,
    int NotCountable,
    int GoldTotal,
    int Recovered)
{
    /// <summary>Questions this census actually scored.</summary>
    internal int Scored => Exact + Undercounts + Overcounts + Unparseable;

    /// <summary>Share of gold items recovered, or null when nothing countable was asked.</summary>
    internal double? Recovery => GoldTotal > 0 ? (double)Recovered / GoldTotal : null;

    /// <summary>
    /// Whether the over-merge guard is clear.
    /// </summary>
    /// <remarks>
    /// Stated as a property rather than left to the reader, because the guard's whole value is that
    /// it binds harder than the headline: a run where overcounts appear does not confirm, whatever
    /// the score does.
    /// </remarks>
    internal bool OverMergeGuardClear => Overcounts == 0;

    internal string Describe()
    {
        // Built as ONE interpolated expression: string.Create's culture overload takes the handler by
        // ref, so a concatenation of interpolated pieces does not bind to it.
        var share = Recovery is { } value
            ? value.ToString("P0", CultureInfo.InvariantCulture)
            : "n/a";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"exact {Exact} · undercounts {Undercounts} · overcounts {Overcounts} · unparseable {Unparseable} · gold recovered {Recovered}/{GoldTotal} ({share})");
    }
}
