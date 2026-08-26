using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The fact-weighted recall budget (Arm A follow-up, 2026-08-21).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this budget exists.</b> The structured split gives an even third each to entities, facts
/// and preferences. Measured on 50 Arithmetic questions, the answer context came out 45% facts / 33%
/// entities / 22% preferences, and every count-and-sum error was in the same direction — too few.
/// A preference has no number in it to count or sum, so two thirds of an arithmetic question's
/// context cannot carry a value. This reallocates the <b>same total</b> toward facts.
/// </para>
/// <para>
/// <b>Why the guard is tested first.</b> The adapter calls <c>FactWeighted</c> directly rather than
/// through <c>For</c>, so it does not inherit <c>For</c>'s non-negative check. At a total of 1 the
/// minor sections floor at 1 each and facts becomes <c>1 - 2 = -1</c>. A negative budget is not a
/// small budget — it is a limit that silently inverts every downstream comparison it reaches.
/// </para>
/// </remarks>
public sealed class FactWeightedBudgetTests
{
    [Fact]
    public void AtTheRealTotalTheSplitIsFiveTwentyFive()
    {
        // 30 is what the harness actually passes (DefaultMaxRelevant).
        var budget = LongMemEvalRecallBudget.FactWeighted(30);

        budget.Entities.Should().Be(5);
        budget.Facts.Should().Be(20);
        budget.Preferences.Should().Be(5);
        budget.Messages.Should().Be(0);
        budget.GraphRag.Should().Be(0);
    }

    [Fact]
    public void ItIsAReallocationNotAnEnlargement()
    {
        // The claim this arm rests on: a score difference is attributable to WHERE the budget went,
        // not to having more of it. If the totals ever diverge, the experiment is confounded and the
        // number means nothing.
        const int Total = 30;

        var even = LongMemEvalRecallBudget.For(LongMemEvalMemoryMode.Structured, Total);
        var weighted = LongMemEvalRecallBudget.FactWeighted(Total);

        (weighted.Entities + weighted.Facts + weighted.Preferences)
            .Should().Be(even.Entities + even.Facts + even.Preferences);
    }

    [Fact]
    public void ItActuallyWeightsTowardFacts()
    {
        var even = LongMemEvalRecallBudget.For(LongMemEvalMemoryMode.Structured, 30);
        var weighted = LongMemEvalRecallBudget.FactWeighted(30);

        weighted.Facts.Should().BeGreaterThan(even.Facts,
            "a fact-weighted budget that did not increase the fact allowance would be a no-op arm");
    }

    [Fact]
    public void TheMinorSectionsKeepAtLeastOneSlot()
    {
        // Not zero, deliberately. A count question still needs the entity that names what is being
        // counted; zeroing a section would test "facts only", which is a different and worse claim.
        var budget = LongMemEvalRecallBudget.FactWeighted(6);

        budget.Entities.Should().Be(1);
        budget.Preferences.Should().Be(1);
        budget.Facts.Should().Be(4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANonPositiveTotalThrowsRatherThanReturningANegativeBudget(int total)
    {
        // The defect this guard closes. Without it the adapter's direct call bypasses For()'s check.
        var build = () => LongMemEvalRecallBudget.FactWeighted(total);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NoReachableTotalProducesANegativeSection()
    {
        // Swept rather than sampled: the repository's own lesson is that rotating lenses SAMPLE shapes
        // instead of exhausting them, so this walks every total the harness could plausibly pass.
        for (var total = 1; total <= 200; total++)
        {
            var budget = LongMemEvalRecallBudget.FactWeighted(total);

            budget.Entities.Should().BeGreaterThanOrEqualTo(0, "total {0}", total);
            budget.Preferences.Should().BeGreaterThanOrEqualTo(0, "total {0}", total);
            budget.Facts.Should().BeGreaterThanOrEqualTo(0,
                "a negative fact budget at total {0} would invert every downstream comparison", total);
        }
    }

    [Fact]
    public void AtTinyTotalsTheMinorSectionsYieldRatherThanOverdraw()
    {
        // The defect the sweep above found, and the reason ThrowIfNegativeOrZero alone was not enough:
        // total 1 is POSITIVE, so it passes the guard, and the floored minor sections then claim two
        // slots a one-slot budget cannot pay for. Capping them at a third makes the minors yield
        // instead, which is exactly what For() already does at these totals.
        LongMemEvalRecallBudget.FactWeighted(1).Facts.Should().Be(1);
        LongMemEvalRecallBudget.FactWeighted(2).Facts.Should().Be(2);
        LongMemEvalRecallBudget.FactWeighted(3).Facts.Should().Be(1);
        LongMemEvalRecallBudget.FactWeighted(3).Entities.Should().Be(1);
    }
}
