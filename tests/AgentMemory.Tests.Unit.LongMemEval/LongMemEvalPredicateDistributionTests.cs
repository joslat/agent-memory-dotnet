using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// J1.2. The observed predicate distribution is the objective anchor for the vocabulary's
/// "completeness" axis, and the held-out slice is what proves the vocabulary generalises rather than
/// fitting the predicates we happened to look at. Both properties depend on the split being
/// deterministic and total, so the split is pure and tested here rather than buried in a query.
/// </summary>
public sealed class LongMemEvalPredicateDistributionTests
{
    private static readonly IReadOnlyList<LongMemEvalPredicateCount> Observed =
    [
        new("bought", 42, 7), new("was_born", 31, 5), new("likes", 28, 6),
        new("visited", 20, 4), new("completed", 17, 4), new("owns", 15, 3),
        new("sold", 11, 3), new("fixed", 9, 2), new("attended", 8, 2),
        new("married", 6, 2), new("moved_to", 5, 2), new("planned", 4, 1),
        new("rated", 3, 1), new("borrowed", 2, 1), new("lent", 1, 1)
    ];

    [Fact]
    public void TheSplitIsTotalAndDisjoint()
    {
        // Every observed predicate must land in exactly one slice, or coverage arithmetic is wrong.
        var split = LongMemEvalPredicateDistribution.Split(Observed, heldOutFraction: 0.2, seed: 42);

        split.Build.Concat(split.HeldOut).Select(item => item.Predicate)
            .Should().BeEquivalentTo(Observed.Select(item => item.Predicate));
        split.Build.Select(item => item.Predicate).Intersect(
            split.HeldOut.Select(item => item.Predicate)).Should().BeEmpty();
    }

    [Fact]
    public void TheSplitIsDeterministicForAGivenSeed()
    {
        // A split that moved between runs would make held-out coverage unreproducible, which is the
        // same defect as an unrepeatable score.
        var first = LongMemEvalPredicateDistribution.Split(Observed, 0.2, seed: 42);
        var second = LongMemEvalPredicateDistribution.Split(Observed, 0.2, seed: 42);

        second.HeldOut.Select(item => item.Predicate)
            .Should().Equal(first.HeldOut.Select(item => item.Predicate));
    }

    [Fact]
    public void TheSplitDependsOnTheSeed()
    {
        var first = LongMemEvalPredicateDistribution.Split(Observed, 0.2, seed: 42);
        var second = LongMemEvalPredicateDistribution.Split(Observed, 0.2, seed: 7);

        second.HeldOut.Select(item => item.Predicate)
            .Should().NotEqual(first.HeldOut.Select(item => item.Predicate));
    }

    [Fact]
    public void TheHeldOutSliceIsNeitherEmptyNorEverything()
    {
        // An empty held-out slice would silently turn the generalisation gate into a no-op.
        var split = LongMemEvalPredicateDistribution.Split(Observed, 0.2, seed: 42);

        split.HeldOut.Should().NotBeEmpty();
        split.Build.Should().NotBeEmpty();
        split.HeldOut.Count.Should().BeLessThan(Observed.Count / 2);
    }

    [Fact]
    public void FrequencyMassIsReportedForBothSlices()
    {
        // A split by predicate can put a rare or a dominant relation in the held-out slice. Reporting
        // the mass makes that visible instead of letting it silently distort the coverage number.
        var split = LongMemEvalPredicateDistribution.Split(Observed, 0.2, seed: 42);

        (split.BuildFactCount + split.HeldOutFactCount).Should()
            .Be(Observed.Sum(item => item.FactCount));
        split.HeldOutFactCount.Should().Be(split.HeldOut.Sum(item => item.FactCount));
    }

    [Fact]
    public void ConsolidationIsReportedAsRawVersusCanonical()
    {
        // The vocabulary's whole claim is that many surface predicates collapse to few canonical ones.
        var summary = new LongMemEvalPredicateDistributionSummary(
            RawPredicateCount: 421,
            CanonicalPredicateCount: 97,
            TotalFactCount: 700,
            OwnerCount: 10,
            Predicates: Observed);

        summary.ConsolidationRatio.Should().BeApproximately(421d / 97d, 0.001);
    }
}
