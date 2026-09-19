using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The counting census — the statistic the identity preregs decide on, which was computed by hand.
/// </summary>
/// <remarks>
/// A statistic derived by a person reading a report is not reproducible, cannot be red-probed, and
/// its guard is a promise rather than a check. These cases pin the direction of error, because
/// direction is the whole point: undercounting means evidence did not arrive, overcounting means
/// evidence arrived that should not have, and the second is the registered Goodhart signature for
/// identity capture.
/// </remarks>
public sealed class CountCensusTests
{
    /// <summary>The real answer shapes, taken verbatim from a paid run.</summary>
    [Theory]
    [InlineData("2 deliveries.", "2 deliveries.", "Exact")]
    [InlineData("3 deliveries.", "1 delivery.", "Undercount")]
    [InlineData("2 deliveries.", "4 deliveries.", "Overcount")]
    [InlineData("4 deliveries.", "2 deliveries were taken at head office (the Calderwick office).",
        "Undercount")]
    [InlineData("3 deliveries.", "I don't have any retrieved record of deliveries at the annexe.",
        "Unparseable")]
    public void TheDirectionOfErrorIsClassified(string gold, string response, string expected) =>
        TypedMemEvalCountCensus.Classify(gold, response).ToString().Should().Be(expected);

    /// <summary>
    /// An asserted zero is a COUNT; an inability to answer is not.
    /// </summary>
    /// <remarks>
    /// Both carry no digit, and reading both as silence loses a real wrong answer — which is exactly
    /// what made the hand-derived baseline disagree with this census. The line is what the sentence
    /// is ABOUT: a claim about the record answers the question, a claim about the assistant's own
    /// reach declines it.
    /// </remarks>
    [Theory]
    [InlineData("No deliveries at the annexe are recorded in the retrieved memory.", "Undercount")]
    [InlineData("No deliveries at a workshop are recorded.", "Undercount")]
    [InlineData("None were taken there.", "Undercount")]
    [InlineData("I don't have any retrieved record of deliveries at the annexe.", "Unparseable")]
    [InlineData("I cannot determine that from the retrieved memory.", "Unparseable")]
    public void AnAssertedZeroIsACountAndARefusalIsNot(string response, string expected) =>
        TypedMemEvalCountCensus.Classify("4 deliveries.", response).ToString().Should().Be(expected);

    /// <summary>
    /// The count is read from the FRONT, because the parenthetical carries other numbers.
    /// </summary>
    /// <remarks>
    /// Taking the largest or last integer would misread precisely the answers that name a resolved
    /// alias — the ones this census exists to examine.
    /// </remarks>
    [Fact]
    public void TheLeadingCountIsRead() =>
        TypedMemEvalCountCensus
            .ParseCount("2 deliveries were taken at the new flat (the place on Ferrow Row), 3 in all")
            .Should().Be(2);

    /// <summary>
    /// A refusal is not an undercount.
    /// </summary>
    /// <remarks>
    /// It declined to count, which is a different defect. Folding refusals into undercounts is what
    /// made the first entity-linking arm's gold denominators disagree, 45 against 42.
    /// </remarks>
    [Fact]
    public void ARefusalIsItsOwnOutcomeRatherThanAnUndercount()
    {
        var census = TypedMemEvalCountCensus.Measure(
            [("3 deliveries.", "I have no retrieved record of that.")]);

        census.Unparseable.Should().Be(1);
        census.Undercounts.Should().Be(0);
    }

    /// <summary>
    /// Recovery is capped per question, so an overcount cannot inflate it.
    /// </summary>
    /// <remarks>
    /// An overcount has not found more gold than exists — it has added something that is not gold.
    /// Letting it raise recovery would let the one failure this census exists to catch improve the
    /// number it is judged by.
    /// </remarks>
    [Fact]
    public void AnOvercountCannotRaiseRecovery()
    {
        var census = TypedMemEvalCountCensus.Measure([("2 deliveries.", "9 deliveries.")]);

        census.Overcounts.Should().Be(1);
        census.Recovered.Should().Be(2, "there were only two to find");
        census.Recovery.Should().Be(1.0);
        census.OverMergeGuardClear.Should().BeFalse();
    }

    /// <summary>A question whose gold is not a count is excluded, not scored as a failure.</summary>
    [Fact]
    public void ANonCountingQuestionIsExcludedFromTheCensus()
    {
        var census = TypedMemEvalCountCensus.Measure(
            [("the Calderwick office", "head office"), ("2 deliveries.", "2 deliveries.")]);

        census.NotCountable.Should().Be(1);
        census.Scored.Should().Be(1);
        census.GoldTotal.Should().Be(2, "a denominator must not absorb rows it never measured");
    }

    /// <summary>
    /// Only shapes that count ITEMS are in the census.
    /// </summary>
    /// <remarks>
    /// Run unrestricted over the existing artifacts, this census reported `arithmetic-sum` and
    /// `arithmetic-delta` as carrying seven and eight overcounts. Their golds are magnitudes — 3,297
    /// and 3,012 — so answering high is an arithmetic error, not two referents merged into one.
    /// Firing the over-merge guard there is a category error, and a guard that fires on every
    /// arithmetic run teaches its reader to skip the line.
    /// </remarks>
    [Theory]
    [InlineData("conjunction-alias-then-count", true)]
    [InlineData("conjunction-value-then-count", true)]
    [InlineData("arithmetic-count", true)]
    [InlineData("arithmetic-sum", false)]
    [InlineData("arithmetic-delta", false)]
    [InlineData("arithmetic-duration", false)]
    [InlineData("conjunction-order-then-value", false)]
    [InlineData(null, false)]
    public void OnlyCountingShapesAreInTheCensus(string? shape, bool included) =>
        TypedMemEvalCountCensus.IsCountingShape(shape).Should().Be(included);

    /// <summary>The guard is clear only when no overcount appears at all.</summary>
    [Fact]
    public void TheOverMergeGuardBindsOnASingleOvercount()
    {
        TypedMemEvalCountCensus
            .Measure([("2 deliveries.", "2 deliveries."), ("2 deliveries.", "3 deliveries.")])
            .OverMergeGuardClear.Should().BeFalse(
                "a run where overcounts appear does not confirm, whatever the score does");
    }
}
