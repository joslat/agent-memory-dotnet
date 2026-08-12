using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The instrument that decides whether the abstention track lives (PLAN 4.2).
/// </summary>
/// <remarks>
/// <para>
/// Per-section diagnostics say why a section came back thin. Everything downstream — calibration,
/// "I don't know", any confidence surface — assumes those numbers <b>mean</b> something. AUC is the
/// test of that assumption, and <b>0.5 is the kill line</b>.
/// </para>
/// <para>
/// Which makes this instrument's own correctness load-bearing in an unusual way: an implementation
/// that reports a flattering number keeps a dead track alive for a quarter. So the tests below are
/// weighted towards the ways an AUC can be wrong while looking fine — ties, one empty class, and
/// inverted ordering.
/// </para>
/// </remarks>
public sealed class SufficiencyAucTests
{
    private static LongMemEvalSufficiencyAuc.Observation Present(double signal) => new(signal, true);
    private static LongMemEvalSufficiencyAuc.Observation Absent(double signal) => new(signal, false);

    [Fact]
    public void PerfectSeparationScoresOne()
    {
        var result = LongMemEvalSufficiencyAuc.Compute(
            [Present(0.9), Present(0.8), Absent(0.2), Absent(0.1)]);

        result.Auc.Should().Be(1.0);
        result.JustifiesAbstentionWork.Should().BeTrue();
    }

    [Fact]
    public void PerfectlyInvertedSeparationScoresZero()
    {
        // Not symmetric with the above in consequence: an AUC of 0 means the signal is a perfect
        // predictor read backwards, which is a wiring bug, not a dead signal. Reporting it as 1.0 --
        // by taking max(auc, 1-auc), a tempting "fix" -- would hide exactly that.
        var result = LongMemEvalSufficiencyAuc.Compute(
            [Present(0.1), Present(0.2), Absent(0.8), Absent(0.9)]);

        result.Auc.Should().Be(0.0);
        result.JustifiesAbstentionWork.Should().BeFalse();
    }

    [Fact]
    public void AConstantSignalScoresExactlyAHalf()
    {
        // THE case that matters. A signal returning the same value everywhere carries no information,
        // and a curve-based implementation over a swept threshold can score such a tie block as if it
        // were ordered. The rank form gives ties their 0.5 and reports the coin flip it is.
        var result = LongMemEvalSufficiencyAuc.Compute(
            [Present(0.5), Present(0.5), Absent(0.5), Absent(0.5)]);

        result.Auc.Should().Be(0.5);
        result.JustifiesAbstentionWork.Should().BeFalse();
        result.TiedObservations.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PartialTiesGetHalfCreditRatherThanFullCredit()
    {
        // One clean pair and one tied pair: 1 + 0.5 out of 2.
        var result = LongMemEvalSufficiencyAuc.Compute(
            [Present(0.9), Present(0.4), Absent(0.4)]);

        result.Auc.Should().BeApproximately(0.75, 1e-9);
    }

    [Fact]
    public void OneEmptyClassIsNotMeasuredRatherThanScoredAHalf()
    {
        // A run where every answer was present never asked the signal to order anything. Returning
        // 0.5 would report "no signal" for a question that was not put -- and 0.5 is the kill line, so
        // that particular default would kill a track on absent evidence.
        var allPresent = LongMemEvalSufficiencyAuc.Compute([Present(0.9), Present(0.2)]);

        allPresent.Auc.Should().BeNull();
        allPresent.JustifiesAbstentionWork.Should().BeFalse();
        allPresent.Describe().Should().Contain("not measured");
    }

    [Fact]
    public void NoObservationsAtAllIsAlsoNotMeasured()
    {
        LongMemEvalSufficiencyAuc.Compute([]).Auc.Should().BeNull();
    }

    [Fact]
    public void TheDescriptionAlwaysStatesBothDenominators()
    {
        // An AUC without its class counts is unreadable: 0.83 over 47 present and 3 absent is three
        // questions' worth of evidence, and the bare number hides that entirely.
        var result = LongMemEvalSufficiencyAuc.Compute(
            [Present(0.9), Present(0.8), Present(0.7), Absent(0.1)]);

        result.Describe().Should().Contain("3 present").And.Contain("1 absent");
    }

    [Fact]
    public void TheJustificationThresholdIsFixedInAdvanceAndSitsAboveTheCoinLine()
    {
        // Stated before any number is seen, so the conclusion cannot be chosen after the fact.
        LongMemEvalSufficiencyAuc.Compute([Present(0.9), Absent(0.1)])
            .JustifiesAbstentionWork.Should().BeTrue();

        // 0.5 exactly -- a coin -- must not justify anything.
        LongMemEvalSufficiencyAuc.Compute([Present(0.5), Absent(0.5)])
            .JustifiesAbstentionWork.Should().BeFalse();
    }

    [Fact]
    public void OrderOfObservationsDoesNotChangeTheResult()
    {
        // Rank computation over an unsorted input is where an off-by-one hides, and it would produce a
        // number that is wrong by a little -- the hardest kind to notice.
        var forward = LongMemEvalSufficiencyAuc.Compute(
            [Present(0.9), Absent(0.3), Present(0.5), Absent(0.7)]);
        var reversed = LongMemEvalSufficiencyAuc.Compute(
            [Absent(0.7), Present(0.5), Absent(0.3), Present(0.9)]);

        forward.Auc.Should().Be(reversed.Auc);
    }

    [Fact]
    public void AKnownHandComputedCaseMatches()
    {
        // Present {0.9, 0.5}, absent {0.7, 0.3}: pairs (0.9>0.7)=1, (0.9>0.3)=1, (0.5<0.7)=0,
        // (0.5>0.3)=1 -> 3/4.
        LongMemEvalSufficiencyAuc.Compute([Present(0.9), Present(0.5), Absent(0.7), Absent(0.3)])
            .Auc.Should().BeApproximately(0.75, 1e-9);
    }
}
