using System.Reflection;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.11. Vote aggregation and quote-forcing — the two halves of Proposal F, tested where they are
/// deterministic.
/// </summary>
/// <remarks>
/// <para>
/// The measurement this feature exists for cannot be taken here (it needs the corpus and paid answer
/// calls), but the <i>mechanism</i> can be pinned completely, and that is where its failure modes live.
/// Two in particular: an aggregation that over-normalises invents a consensus the model never had, and
/// a parser that discards an unformatted answer converts a formatting miss into a scored memory failure.
/// </para>
/// <para>
/// The void witness gets its own tests because 30.1 turned it from a formality into a live outcome:
/// seeding was measured to <b>halve</b> answer variance on this deployment, so distinct-seeded votes may
/// agree for reasons that have nothing to do with the model's confidence.
/// </para>
/// </remarks>
public sealed class AnswerVotingAndQuoteForcingTests
{
    private static readonly Assembly Tool =
        typeof(AgentMemory.LongMemEval.LongMemEvalPreparationManifest).Assembly;

    private static readonly Type Vote =
        Tool.GetType("AgentMemory.LongMemEval.LongMemEvalAnswerVote")!;

    private static readonly Type Quote =
        Tool.GetType("AgentMemory.LongMemEval.LongMemEvalQuoteForcing")!;

    private static object Aggregate(params string[] votes)
    {
        var method = Vote.GetMethod("Aggregate", BindingFlags.Public | BindingFlags.Static)!;
        try
        {
            return method.Invoke(null, [(IReadOnlyList<string>)votes])!;
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException!;
        }
    }

    private static T Get<T>(object result, string name) =>
        (T)result.GetType().GetProperty(name)!.GetValue(result)!;

    private static object ParseQuote(string? response) =>
        Quote.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [response])!;

    // ── aggregation ───────────────────────────────────────────────────

    [Fact]
    public void AUnanimousVoteReturnsThatAnswer()
    {
        var result = Aggregate("Paris", "Paris", "Paris");

        Get<string>(result, "Answer").Should().Be("Paris");
        Get<int>(result, "WinningVotes").Should().Be(3);
        Get<bool>(result, "AllIdentical").Should().BeTrue();
    }

    [Fact]
    public void AMajorityWins()
    {
        var result = Aggregate("Paris", "Lyon", "Paris");

        Get<string>(result, "Answer").Should().Be("Paris");
        Get<bool>(result, "HasMajority").Should().BeTrue();
        Get<int>(result, "DistinctAnswers").Should().Be(2);
    }

    [Fact]
    public void AThreeWaySplitIsReportedRatherThanSilentlyResolved()
    {
        // Spending an LLM tiebreak costs money, so that decision must not be made implicitly inside an
        // aggregation helper. The caller is told there was no majority and decides.
        var result = Aggregate("Paris", "Lyon", "Nice");

        Get<bool>(result, "HasMajority").Should().BeFalse();
        Get<int>(result, "DistinctAnswers").Should().Be(3);
    }

    [Fact]
    public void ASplitBreaksTowardTheFirstVote()
    {
        // The first vote is the unseeded-equivalent call, so a fully-split set returns exactly what a
        // single-vote run would have returned -- which keeps a voted run comparable with the archive.
        Get<string>(Aggregate("Paris", "Lyon", "Nice"), "Answer").Should().Be("Paris");
    }

    [Fact]
    public void ClusteringIgnoresCaseWhitespaceAndTrailingPunctuation()
    {
        // "Paris." and "paris" are one answer. Counting them as two would report a disagreement the
        // model never had, and the primary claim of this whole feature is about disagreement.
        var result = Aggregate("Paris.", " paris ", "PARIS");

        Get<bool>(result, "AllIdentical").Should().BeTrue();
        Get<int>(result, "DistinctAnswers").Should().Be(1);
    }

    [Fact]
    public void TheWinnerKeepsItsOriginalSpelling()
    {
        // The judge reads this string. Handing it a normalised form would score a different answer than
        // the model gave.
        Get<string>(Aggregate("Paris.", "paris", "PARIS"), "Answer").Should().Be("Paris.");
    }

    [Fact]
    public void ClusteringDoesNotMergeAnswersThatDifferInSubstance()
    {
        // Deliberately conservative: no article-stripping, no stemming, no reordering. Each of those
        // merges answers a judge would score differently, turning a real disagreement into an invented
        // consensus.
        Get<int>(Aggregate("the Eiffel Tower", "Eiffel Tower", "a tower"), "DistinctAnswers")
            .Should().Be(3);
    }

    [Fact]
    public void AggregatingNoVotesIsAnError()
    {
        // Silently returning an empty answer would put a blank into the judge and score it as wrong,
        // attributing a harness bug to memory.
        var act = () => Aggregate();

        act.Should().Throw<ArgumentException>();
    }

    // ── the void witness ──────────────────────────────────────────────

    [Fact]
    public void TheVoidWitnessFiresWhenTheSamplerIsNotSampling()
    {
        var isVoid = (bool)Vote.GetMethod("IsVoidBySampler", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [45, 50])!;

        isVoid.Should().BeTrue("45 of 50 identical is above the pre-registered 80% threshold");
    }

    [Fact]
    public void TheVoidWitnessStaysQuietWhenVotesGenuinelyVary()
    {
        var isVoid = (bool)Vote.GetMethod("IsVoidBySampler", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [30, 50])!;

        isVoid.Should().BeFalse();
    }

    [Fact]
    public void TheThresholdIsThePreRegisteredEightyPercent()
    {
        Vote.GetField("VoidWitnessThreshold", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null).Should().Be(0.8);
    }

    // ── seeds ─────────────────────────────────────────────────────────

    [Fact]
    public void SeedsAreDistinctPerVoteAndDerivedFromOneRecordedNumber()
    {
        var seeds = (IReadOnlyList<int?>)Vote
            .GetMethod("SeedsFor", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [(int?)100, 3])!;

        seeds.Should().Equal(100, 101, 102);
    }

    [Fact]
    public void NoBaseSeedMeansTheHistoricalUnseededCall()
    {
        // The off state, byte-identical: every archived run was unseeded, and a default that quietly
        // seeded would make new runs incomparable with all of them.
        var seeds = (IReadOnlyList<int?>)Vote
            .GetMethod("SeedsFor", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [(int?)null, 3])!;

        seeds.Should().Equal([null, null, null]);
    }

    // ── quote forcing ─────────────────────────────────────────────────

    [Fact]
    public void AWellFormedResponseYieldsBothHalves()
    {
        var parsed = ParseQuote("EVIDENCE: \"the user works at Initech\"\nANSWER: Initech");

        Get<string>(parsed, "Answer").Should().Be("Initech");
        Get<string?>(parsed, "Evidence").Should().Be("the user works at Initech");
        Get<bool>(parsed, "FormatHonoured").Should().BeTrue();
    }

    [Fact]
    public void TheNoneFoundEscapeIsRecordedAsAnAdmission()
    {
        // The alternative to admitting absence is inventing presence, so the escape exists -- and is
        // distinguished from a missing evidence line, which is a formatting miss rather than an
        // admission.
        var parsed = ParseQuote("EVIDENCE: NONE FOUND\nANSWER: I do not have that information.");

        Get<bool>(parsed, "EvidenceAbsent").Should().BeTrue();
        Get<string>(parsed, "Answer").Should().Be("I do not have that information.");
    }

    [Fact]
    public void AnUnformattedResponseKeepsItsAnswerAndSaysTheFormatWasMissed()
    {
        // A model that ignored the format still answered. Discarding that would convert a formatting
        // miss into a scored memory failure -- measuring instruction-following where the run measures
        // memory.
        var parsed = ParseQuote("Initech");

        Get<string>(parsed, "Answer").Should().Be("Initech");
        Get<bool>(parsed, "FormatHonoured").Should().BeFalse();
    }

    [Fact]
    public void AnEvidenceLineWithNoAnswerLineIsAFormatMiss()
    {
        var parsed = ParseQuote("EVIDENCE: \"something\"");

        Get<bool>(parsed, "FormatHonoured").Should().BeFalse();
    }

    [Fact]
    public void AnEmptyResponseIsAFormatMissWithNoAnswer()
    {
        var parsed = ParseQuote("   ");

        Get<string>(parsed, "Answer").Should().BeEmpty();
        Get<bool>(parsed, "FormatHonoured").Should().BeFalse();
    }

    [Fact]
    public void TheQuoteForcingPromptExtendsTheBaseRatherThanReplacingIt()
    {
        // The base prompt already carries "do not claim information absent from memory" -- the clause
        // this format exists to make checkable. Replacing it would drop the instruction the format
        // enforces.
        var basePrompt = "Answer the question using only the retrieved memory below.";
        var prompt = (string)Quote.GetMethod("SystemPrompt", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [basePrompt])!;

        prompt.Should().StartWith(basePrompt);
        prompt.Should().Contain("EVIDENCE:");
        prompt.Should().Contain("NONE FOUND");
        prompt.Should().Contain("ANSWER:");
    }
}
