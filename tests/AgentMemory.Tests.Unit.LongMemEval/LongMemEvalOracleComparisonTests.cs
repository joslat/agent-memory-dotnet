using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The decomposed-vs-monolithic oracle comparison (R-oracle).
/// </summary>
/// <remarks>
/// <para>
/// The comparison decides whether to build a whole answering architecture, and it is arithmetic over
/// paired verdicts — which is precisely the kind of code that returns a plausible number while being
/// wrong. These tests exist because the run itself cannot check it: a mis-counted discordant pair
/// produces a clean-looking result and no error.
/// </para>
/// </remarks>
public sealed class LongMemEvalOracleComparisonTests
{
    private static LongMemEvalOraclePair Pair(
        string id, bool? mono, bool? dec, int subQuestions = 3) =>
        new(id, mono, dec, subQuestions);

    [Fact]
    public void OnlyDiscordantPairsCarryEvidence()
    {
        // McNemar's whole point. An agreeing majority says nothing about a DIFFERENCE between the
        // arms, and reporting an accuracy delta instead would let 40 agreeing questions dilute a real
        // 5-question effect in either direction.
        var comparison = LongMemEvalOracleComparison.From(
        [
            Pair("a", true, true),
            Pair("b", true, true),
            Pair("c", false, false),
            Pair("d", false, true),
            Pair("e", true, false),
            Pair("f", false, true),
        ]);

        comparison.Discordant.Should().Be((2, 1));
        comparison.BothCorrect.Should().Be(2);
        comparison.BothWrong.Should().Be(1);
        comparison.Comparable.Should().Be(6);
    }

    [Fact]
    public void AnInconclusiveVerdictIsExcludedRatherThanCountedWrong()
    {
        // Folding "the judge could not be parsed" into "the arm was wrong" would make a judge that
        // struggles with composed answers read as decomposition failing -- and the decomposed arm
        // produces a differently-shaped answer, so it is exactly the arm at risk of that.
        var comparison = LongMemEvalOracleComparison.From(
        [
            Pair("a", true, null),
            Pair("b", null, true),
            Pair("c", true, false),
        ]);

        comparison.Inconclusive.Should().Be(2);
        comparison.Comparable.Should().Be(1);
        comparison.Discordant.Should().Be((0, 1));
    }

    [Fact]
    public void ARunThatDecomposedNothingIsVoid()
    {
        // THE witness. A decomposer that hands back the original question produces an arm identical
        // to the control; the comparison then reports "no difference" -- a statement about the
        // decomposer having never run, wearing the authority of a controlled experiment. Six runs of
        // 7.6 were voided to this exact shape before a witness was added.
        var comparison = LongMemEvalOracleComparison.From(
        [
            Pair("a", true, true, subQuestions: 1),
            Pair("b", false, false, subQuestions: 1),
            Pair("c", true, false, subQuestions: 1),
        ]);

        comparison.ActuallyDecomposed.Should().Be(0);
        comparison.IsVoid.Should().BeTrue();
        comparison.Describe().Should().StartWith("VOID");
    }

    [Fact]
    public void OneGenuinelyDecomposedQuestionIsEnoughToLiftTheVoid()
    {
        // The witness asks whether the mechanism ran at all, not whether it ran often. Requiring a
        // proportion would silently convert a wiring check into a power calculation, and this project
        // has already shipped one gate that returned a verdict about sample size.
        var comparison = LongMemEvalOracleComparison.From(
        [
            Pair("a", true, true, subQuestions: 1),
            Pair("b", false, true, subQuestions: 2),
        ]);

        comparison.ActuallyDecomposed.Should().Be(1);
        comparison.IsVoid.Should().BeFalse();
    }

    [Fact]
    public void AnEmptyRunIsVoidRatherThanAPerfectTie()
    {
        // Zero discordant pairs out of zero observations is not a null result.
        var comparison = LongMemEvalOracleComparison.From([]);

        comparison.Comparable.Should().Be(0);
        comparison.IsVoid.Should().BeTrue();
    }

    [Fact]
    public void TheWitnessCountsOnlyComparablePairs()
    {
        // A question the decomposer split but whose verdict was unusable proves the decomposer ran and
        // contributes no evidence. Letting it satisfy the witness would license a conclusion drawn
        // from zero usable observations -- the void condition passing on the strength of a row that
        // was excluded from the denominator.
        var comparison = LongMemEvalOracleComparison.From(
        [
            Pair("a", null, null, subQuestions: 4),
            Pair("b", true, true, subQuestions: 1),
        ]);

        comparison.ActuallyDecomposed.Should().Be(0);
        comparison.IsVoid.Should().BeTrue();
    }

    [Fact]
    public void BothWrongIsReportedSeparatelyFromTheDiscordantCounts()
    {
        // The both-wrong bucket holds the oracle-impossible questions -- four in the archive, each
        // failed 36/36 with perfect context. They are unreachable by ANY answering strategy, so
        // letting them into a denominator would understate decomposition against a target nothing
        // could hit.
        var comparison = LongMemEvalOracleComparison.From(
        [
            Pair("352ab8bd", false, false),
            Pair("58470ed2", false, false),
            Pair("live", false, true),
        ]);

        comparison.BothWrong.Should().Be(2);
        comparison.Discordant.Should().Be((1, 0));
        comparison.Describe().Should().Contain("both wrong 2");
    }

    [Fact]
    public void TheSummaryCarriesDenominatorsNotABarePercentage()
    {
        // A percentage with no n is how a 3-question result gets quoted as a headline.
        var comparison = LongMemEvalOracleComparison.From([Pair("a", false, true)]);

        var line = comparison.Describe();

        line.Should().Contain("comparable 1");
        line.Should().Contain("decomposed-only 1");
        line.Should().NotContain("%");
    }
}
