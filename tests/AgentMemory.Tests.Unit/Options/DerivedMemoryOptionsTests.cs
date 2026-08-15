using AgentMemory.Abstractions.Options;
using FluentAssertions;

// OptionsTests, not Options: a namespace segment named Options shadows Microsoft.Extensions.Options
// for every file in the assembly that writes the unqualified `Options.Create(...)`, which is most of
// them. Every sibling in this folder does the same.
namespace AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// 30.6 step 1. The defaults, asserted — because two of them are load-bearing and look arbitrary.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DerivationOperators.Duration"/> and <see cref="DerivationOperators.Sum"/> are absent from
/// the default operator set for reasons that would evaporate the first time someone "tidied up" the
/// enum into a convenient <c>All</c>. Duration needs real dates and the current corpus stamps
/// <c>UnixEpoch + counter</c>, so durations computed on it are fiction with a plausible shape. Sum needs
/// to know a predicate is additive; adding three temperatures yields a number whose arithmetic is
/// perfectly correct and whose meaning is nonsense — no test catches that, which is exactly why the
/// allowlist starts empty.
/// </para>
/// </remarks>
public sealed class DerivedMemoryOptionsTests
{
    [Fact]
    public void TheFeatureIsOff()
    {
        new DerivedMemoryOptions().Enabled.Should().BeFalse();
    }

    [Fact]
    public void ExtractionOptionsCarriesTheAccountantOff()
    {
        new ExtractionOptions().DerivedMemory.Enabled.Should().BeFalse();
    }

    [Fact]
    public void TheDefaultOperatorsAreTheFourThatNeedNothingExtra()
    {
        new DerivedMemoryOptions().Operators.Should().Be(
            DerivationOperators.Count | DerivationOperators.Delta |
            DerivationOperators.Latest | DerivationOperators.SetEnumeration);
    }

    [Fact]
    public void DurationIsNotOnByDefaultBecauseTheCorpusHasNoRealDates()
    {
        new DerivedMemoryOptions().Operators.HasFlag(DerivationOperators.Duration)
            .Should().BeFalse("durations over UnixEpoch+counter timestamps are fiction");
    }

    [Fact]
    public void SumIsNotOnByDefaultBecauseAdditivityCannotBeInferred()
    {
        new DerivedMemoryOptions().Operators.HasFlag(DerivationOperators.Sum)
            .Should().BeFalse("summing non-additive quantities is arithmetically correct and meaningless");
    }

    [Fact]
    public void TheAdditiveAllowlistStartsEmptySoSumNeverRunsUnasked()
    {
        new DerivedMemoryOptions().AdditivePredicateKeys.Should().BeEmpty();
    }

    [Fact]
    public void TheAllowlistIsMutableSoAHostCanActuallyPopulateIt()
    {
        // The issue-#100 lesson in its exact shape: sub-options that cannot be set through
        // configureMemory are options no host can use.
        var options = new DerivedMemoryOptions();

        options.AdditivePredicateKeys.Add("fish_count");

        options.AdditivePredicateKeys.Should().ContainSingle().Which.Should().Be("fish_count");
    }

    [Fact]
    public void TheCapsAreTheDocumentedOnes()
    {
        var options = new DerivedMemoryOptions();

        options.MaxDerivedFactsPerBatch.Should().Be(32);
        options.MaxGroupFanIn.Should().Be(200);
        options.MaxEnumerationItems.Should().Be(10);
        options.DerivedFactConfidence.Should().Be(0.9);
    }

    [Fact]
    public void EveryOperatorFlagIsADistinctPowerOfTwo()
    {
        // A [Flags] enum with a duplicated or non-power-of-two value silently merges two operators, and
        // the symptom is an operator that "never runs" while its flag reads as set.
        var values = Enum.GetValues<DerivationOperators>()
            .Where(value => value != DerivationOperators.None)
            .Select(value => (int)value)
            .ToList();

        values.Should().OnlyHaveUniqueItems();
        values.Should().OnlyContain(value => (value & (value - 1)) == 0);
    }
}
