using System.Globalization;
using System.Text;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 30.11. Aggregates N sampled answers into one, and reports how much they disagreed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pre-registered primary claim is that the BAND NARROWS</b> across repeat runs, not that point
/// accuracy rises. This project has measured a 14-point spread between two identical accepted runs; on
/// n=50 a single question is two points, so a point comparison between two runs of anything is noise
/// wearing a decimal. Voting is a variance-reduction technique, and variance is what it should be
/// judged on.
/// </para>
/// <para>
/// <b>The void witness is a live outcome here, not a formality.</b> Proposal F assumed the provider's
/// forced temperature 1.0 <i>is</i> the sampler and gave each vote a distinct seed. 30.1's probe then
/// measured that seeding <b>halves</b> answer variance on <c>gpt-5.5</c> (19 → 8 distinct texts of 24),
/// so distinct-seeded votes may collapse into agreement that reflects the seed rather than the model's
/// confidence. If the votes are byte-identical on more than 80% of questions, the sampler is not
/// sampling: that is a measured property of the provider, and the pre-registered response is to record
/// it and stop rather than to report a narrowed band the voting did not cause.
/// </para>
/// </remarks>
internal static class LongMemEvalAnswerVote
{
    /// <summary>The share of byte-identical vote sets above which the run declares itself void.</summary>
    internal const double VoidWitnessThreshold = 0.8;

    /// <summary>
    /// Picks the winner from a set of sampled answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clustering is on the <b>normalised</b> text — trimmed, case-folded, inner whitespace collapsed,
    /// trailing punctuation dropped — because "Paris." and "paris" are one answer and counting them as
    /// two would report disagreement the model never had. The winner is returned in its <b>original</b>
    /// spelling, since the judge reads it.
    /// </para>
    /// <para>
    /// Ties break toward the <b>first</b> vote, which is the unseeded-equivalent call and therefore the
    /// one comparable with every archived single-vote run. A three-way split with no majority is
    /// reported as such (<see cref="AnswerVoteResult.HasMajority"/>) rather than silently resolved, so
    /// the caller can decide whether to spend an LLM tiebreak — a decision that costs money and must not
    /// be made implicitly inside an aggregation helper.
    /// </para>
    /// </remarks>
    public static AnswerVoteResult Aggregate(IReadOnlyList<string> votes)
    {
        ArgumentNullException.ThrowIfNull(votes);
        if (votes.Count == 0)
            throw new ArgumentException("Aggregating zero votes has no answer.", nameof(votes));

        var clusters = new List<(string Normalised, string First, int Count, int FirstIndex)>();
        for (var index = 0; index < votes.Count; index++)
        {
            var normalised = Normalise(votes[index]);
            var at = clusters.FindIndex(c => string.Equals(c.Normalised, normalised, StringComparison.Ordinal));
            if (at < 0) clusters.Add((normalised, votes[index], 1, index));
            else clusters[at] = clusters[at] with { Count = clusters[at].Count + 1 };
        }

        // Most votes wins; earliest vote breaks a tie, so a fully-split set returns the first answer --
        // exactly what a single-vote run would have returned.
        var winner = clusters
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.FirstIndex)
            .First();

        return new AnswerVoteResult
        {
            Answer = winner.First,
            WinningVotes = winner.Count,
            TotalVotes = votes.Count,
            DistinctAnswers = clusters.Count,
            HasMajority = winner.Count * 2 > votes.Count,
            AllIdentical = clusters.Count == 1,
        };
    }

    /// <summary>
    /// Normalises an answer for clustering only. Never for display, and never for the judge.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: case, surrounding whitespace, inner whitespace runs, and trailing
    /// sentence punctuation. It does <b>not</b> strip articles, stem, or reorder — each of those would
    /// merge answers that differ in ways a judge would score differently, turning a disagreement the
    /// model genuinely had into a consensus the aggregation invented.
    /// </remarks>
    internal static string Normalise(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return string.Empty;

        var builder = new StringBuilder(answer.Length);
        var pendingSpace = false;
        foreach (var character in answer.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(character)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(character);
        }

        var text = builder.ToString();
        return text.TrimEnd('.', '!', '?', ',', ';', ':');
    }

    /// <summary>
    /// True when the sampler is not sampling: too many questions produced identical votes.
    /// </summary>
    /// <remarks>
    /// Reported per run, not per question. One question whose answer is a single word will agree with
    /// itself no matter what the sampler does; the property of interest is whether the <i>set</i> of
    /// questions shows any variation at all.
    /// </remarks>
    public static bool IsVoidBySampler(int questionsWithIdenticalVotes, int totalQuestions) =>
        totalQuestions > 0
        && (double)questionsWithIdenticalVotes / totalQuestions > VoidWitnessThreshold;

    /// <summary>
    /// The seeds to use for N votes, given a base seed.
    /// </summary>
    /// <remarks>
    /// Distinct per vote and derived from the base, so a run is reproducible from one recorded number
    /// while its votes still differ. Returns nulls when no base seed is configured — the historical,
    /// byte-identical call — so the off state remains the unseeded provider default.
    /// </remarks>
    public static IReadOnlyList<int?> SeedsFor(int? baseSeed, int votes)
    {
        if (votes <= 0) throw new ArgumentOutOfRangeException(nameof(votes), votes, "At least one vote.");
        if (baseSeed is null) return [.. Enumerable.Repeat((int?)null, votes)];
        return [.. Enumerable.Range(0, votes).Select(offset => (int?)(baseSeed.Value + offset))];
    }
}

/// <summary>What a vote produced, and how much the votes disagreed.</summary>
/// <remarks>
/// The disagreement figures are the point. A winner reported without them is a single answer with extra
/// steps; with them, a reader can tell a three-nil consensus from a two-one split — and the second is
/// exactly where a confident wrong answer hides.
/// </remarks>
internal sealed record AnswerVoteResult
{
    /// <summary>The winning answer, in its original spelling.</summary>
    public required string Answer { get; init; }

    /// <summary>How many votes the winner received.</summary>
    public required int WinningVotes { get; init; }

    /// <summary>How many votes were cast.</summary>
    public required int TotalVotes { get; init; }

    /// <summary>How many distinct answers appeared, after normalisation.</summary>
    public required int DistinctAnswers { get; init; }

    /// <summary>True when the winner took more than half the votes.</summary>
    public required bool HasMajority { get; init; }

    /// <summary>True when every vote agreed — the per-question input to the void witness.</summary>
    public required bool AllIdentical { get; init; }

    /// <summary>A compact record for the run artifact.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{WinningVotes}/{TotalVotes} votes, {DistinctAnswers} distinct");
}
