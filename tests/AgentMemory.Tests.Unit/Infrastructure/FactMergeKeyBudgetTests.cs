using System.Linq;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// L11. The fact merge key is a <b>composite</b> of four properties, so its budget is the combined
/// size — not four independent budgets.
/// </summary>
/// <remarks>
/// Facts MERGE on <c>{subject_key, predicate_key, object_key, owner_key}</c>. Checking each part
/// against the full budget separately would admit a composite roughly four times over it, which is
/// the failure the guard exists to prevent.
/// <para>
/// Summing is <b>deliberately conservative</b>. Neo4j's documented bound is on the index key, and
/// this codebase has not measured how a composite key is encoded, so the guard budgets the sum of the
/// parts rather than asserting an internal it cannot verify. Erring strict costs only pathological
/// values; erring loose reproduces the opaque driver failure.
/// </para>
/// </remarks>
public sealed class FactMergeKeyBudgetTests
{
    [Fact]
    public void OrdinaryTripleIsAccepted()
    {
        var act = () => IndexKeyBudget.EnsureCompositeIndexable(
            [("subject_key", "ada lovelace"), ("predicate_key", "worked_on"), ("object_key", "the analytical engine")],
            "fact-1");

        act.Should().NotThrow();
    }

    [Fact]
    public void PartsThatAreIndividuallyFineButJointlyOversizedAreRejected()
    {
        // The load-bearing case: each half is under the budget, the composite is not.
        var half = new string('a', (IndexKeyBudget.MaxIndexedBytes / 2) + 8);

        var act = () => IndexKeyBudget.EnsureCompositeIndexable(
            [("subject_key", half), ("object_key", half)], "fact-1");

        act.Should().Throw<MemoryException>().WithMessage("*fact-1*");
    }

    [Fact]
    public void ExactlyAtTheBudgetIsAccepted()
    {
        var act = () => IndexKeyBudget.EnsureCompositeIndexable(
            [("subject_key", new string('a', IndexKeyBudget.MaxIndexedBytes))], "fact-1");

        act.Should().NotThrow();
    }

    [Fact]
    public void MessageNamesTheOffendingPartsSoTheValueCanBeFound()
    {
        var oversized = new string('a', IndexKeyBudget.MaxIndexedBytes + 1);

        var act = () => IndexKeyBudget.EnsureCompositeIndexable(
            [("subject_key", "short"), ("object_key", oversized)], "fact-9");

        act.Should().Throw<MemoryException>()
           .WithMessage("*object_key*").WithMessage("*fact-9*");
    }

    [Fact]
    public void MeasuresUtf8BytesRatherThanCharacters()
    {
        // Four bytes per character: a character-count check would admit this and the index would
        // then reject it, which is exactly the failure being prevented.
        var fourByte = string.Concat(
            Enumerable.Repeat("\U0001F600", (IndexKeyBudget.MaxIndexedBytes / 4) + 1));

        var act = () => IndexKeyBudget.EnsureCompositeIndexable([("subject_key", fourByte)], "fact-1");

        act.Should().Throw<MemoryException>();
    }

    [Fact]
    public void NullAndEmptyPartsContributeNothing()
    {
        var act = () => IndexKeyBudget.EnsureCompositeIndexable(
            [("subject_key", null), ("predicate_key", ""), ("object_key", "x")], "fact-1");

        act.Should().NotThrow();
    }

    /// <summary>The skip-decision used by the bootstrap backfill, which must not throw.</summary>
    [Fact]
    public void ExceedsCompositeBudgetAnswersTheSameQuestionWithoutThrowing()
    {
        var oversized = new string('a', IndexKeyBudget.MaxIndexedBytes + 1);

        IndexKeyBudget.ExceedsCompositeBudget([("subject_key", oversized)]).Should().BeTrue();
        IndexKeyBudget.ExceedsCompositeBudget([("subject_key", "fine")]).Should().BeFalse();
    }
}
