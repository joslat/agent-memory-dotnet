using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// J1.5 gate 1. The banded held-out coverage statistic, and proof it can fail.
/// </summary>
/// <remarks>
/// The first version of this gate failed at 15.4 points and was root-caused to skew rather than to a
/// real generalisation gap — coverage over a long tail of one-fact predicates is dominated by the
/// tail, so whichever slice draws more singletons looks worse regardless of the vocabulary.
/// <para>
/// A gate refined after it failed is exactly the kind that needs proof it still bites. These tests
/// exist so "the gate passes" is a claim about the vocabulary and not about a statistic that cannot
/// return anything else.
/// </para>
/// </remarks>
public sealed class PredicateCoverageBandTests
{

    [Fact]
    public void ADeliberateQueryStopFormCountsAsKnownVocabulary()
    {
        // The mistake the first run of this gate made. `has` and `is` are stop forms: the lexicon
        // refuses to RESOLVE them so a question mentioning "is" cannot expand into the whole graph.
        // They are still legitimate STORED predicates - 2,701 facts between them in the measured
        // graph - and counting them as unknown vocabulary read the gate down to 81.5%.
        var bands = LongMemEvalPredicateDistribution.CoverageBands(
            [Count("has", 1583), Count("is", 1118), Count("was", 33)]);

        var bound = bands.Single(band => band.Band == "10+");
        bound.Coverage.Should().Be(1d);
        bound.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void AnUnknownHighFrequencyPredicateFailsTheBoundBand()
    {
        // The load-bearing case. If this band could not drop below 100%, the gate would be
        // decoration.
        var bands = LongMemEvalPredicateDistribution.CoverageBands(
            [Count("defenestrated", 40), Count("bought", 30)]);

        var bound = bands.Single(band => band.Band == "10+");
        bound.PredicateCount.Should().Be(2);
        bound.ResolvedCount.Should().Be(1);
        bound.Coverage.Should().BeApproximately(0.5, 1e-9);
        bound.Unresolved.Should().ContainSingle().Which.Should().Be("defenestrated");
    }

    [Fact]
    public void AKnownHighFrequencyPredicatePassesTheBoundBand()
    {
        var bands = LongMemEvalPredicateDistribution.CoverageBands(
            [Count("bought", 40), Count("sold", 30)]);

        bands.Single(band => band.Band == "10+").Coverage.Should().Be(1d);
    }

    [Fact]
    public void TailMissesDoNotTouchTheBoundBand()
    {
        // The whole point of banding. An unknown singleton is reported, never allowed to drag the
        // bound band down — that conflation is what produced the false 15.4-point failure.
        var bands = LongMemEvalPredicateDistribution.CoverageBands(
            [Count("bought", 40), Count("defenestrated", 1)]);

        bands.Single(band => band.Band == "10+").Coverage.Should().Be(1d);
        bands.Single(band => band.Band == "1").Coverage.Should().Be(0d);
        bands.Single(band => band.Band == "1").Unresolved.Should().Contain("defenestrated");
    }

    [Fact]
    public void AnEmptyBandCountsAsCoveredRatherThanZero()
    {
        // A band with no members must not read as a failure: "nothing to cover" and "covered
        // nothing" are different, and only one of them should stop a release.
        var bands = LongMemEvalPredicateDistribution.CoverageBands([Count("bought", 40)]);

        bands.Single(band => band.Band == "2").PredicateCount.Should().Be(0);
        bands.Single(band => band.Band == "2").Coverage.Should().Be(1d);
    }

    [Fact]
    public void EveryPredicateLandsInExactlyOneBand()
    {
        // Bands must partition. A gap would silently exempt a predicate from the gate; an overlap
        // would let one failure be masked by another band's pass.
        LongMemEvalPredicateCount[] predicates =
            [Count("a", 1), Count("b", 2), Count("c", 3), Count("d", 9), Count("e", 10), Count("f", 99)];

        LongMemEvalPredicateDistribution.CoverageBands(predicates)
            .Sum(band => band.PredicateCount)
            .Should().Be(predicates.Length);
    }

    private static LongMemEvalPredicateCount Count(string predicate, int facts) =>
        new(predicate, facts, OwnerCount: 1);
}
