using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Selecting which memory types a run evaluates — the sampling side of the typed track.
/// </summary>
/// <remarks>
/// <para>
/// Per-type <i>reporting</i> already existed; per-type <i>sampling</i> did not, and the two are not
/// interchangeable. A 50-question stratified sample yields roughly 6 <c>single-session-assistant</c>
/// questions, so an episodic figure taken from it moves 16.7 points per item — while two runs of an
/// identical configuration have been measured 25 points apart on the full 50. Slicing a mixed sample
/// produces a per-type number that cannot distinguish a real effect from extraction nondeterminism.
/// </para>
/// <para>
/// <c>ExternalBenchmarkOptions.IncludeQuestionTypes</c> has been available since AgentEval 0.20 and
/// <b>no caller passed it</b> — a dead option, which is the same defect class this repo has hunted
/// before. These tests cover the wiring and, more importantly, the refusals.
/// </para>
/// </remarks>
public sealed class MemoryTypeSelectionTests
{
    [Fact]
    public void NoSelectionMeansNoFilter()
    {
        // Null is the sampler's "everything" value. Every sealed base was recorded this way, so the
        // unselected path must stay exactly what it was rather than becoming an explicit full list.
        LongMemEvalMemoryTypeSelection.TaskTypesFor([]).Should().BeNull();
    }

    [Fact]
    public void EpisodicSelectsTheAssistantTaskLabel()
    {
        // The concrete case Phase 8 needs: AssistantContentMode targets exactly the questions asking
        // what the assistant said or did, and this is the label carrying them.
        LongMemEvalMemoryTypeSelection.TaskTypesFor(["episodic"])
            .Should().BeEquivalentTo(["single-session-assistant"]);
    }

    [Fact]
    public void TemporalSelectsBothOfItsLabels()
    {
        // One memory type spans several task labels. Returning only the first would silently halve the
        // sample while the run still reported itself as covering the type.
        LongMemEvalMemoryTypeSelection.TaskTypesFor(["temporal"])
            .Should().BeEquivalentTo(["temporal-reasoning", "knowledge-update"]);
    }

    [Fact]
    public void SeveralTypesUnionTheirLabelsWithoutDuplicates()
    {
        var selected = LongMemEvalMemoryTypeSelection.TaskTypesFor(["episodic", "temporal"]);

        selected.Should().BeEquivalentTo(
            ["single-session-assistant", "temporal-reasoning", "knowledge-update"]);
        selected.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SelectionIsCaseInsensitive()
    {
        LongMemEvalMemoryTypeSelection.TaskTypesFor(["Episodic"])
            .Should().BeEquivalentTo(["single-session-assistant"]);
    }

    [Fact]
    public void TheSameRequestAlwaysProducesTheSameOrder()
    {
        // The selection reaches a run fingerprint. An order that depended on dictionary enumeration
        // would make two identical runs look like different configurations.
        LongMemEvalMemoryTypeSelection.TaskTypesFor(["temporal", "episodic"])
            .Should().Equal(LongMemEvalMemoryTypeSelection.TaskTypesFor(["episodic", "temporal"]));
    }

    // ── the refusals, which are the point ────────────────────────────────

    [Fact]
    public void AskingForProceduralIsRejectedRatherThanQuietlyIgnored()
    {
        // THE refusal. LongMemEval-S is chat QA: no build commands, no tool invocations, no fix
        // trajectories. Returning an empty filter would widen the run back to every question and let
        // the result be published as a procedural number -- the exact metric substitution the taxonomy
        // file exists to prevent.
        var act = () => LongMemEvalMemoryTypeSelection.TaskTypesFor(["procedural"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*procedural*");
    }

    [Fact]
    public void AskingForMetamemoryIsRejectedAndPointsAtAbstention()
    {
        // Metamemory is real and this dataset does score it -- through abstention questions, which are
        // selected by a different mechanism. The error has to say so, or it reads as "not supported".
        var act = () => LongMemEvalMemoryTypeSelection.TaskTypesFor(["metamemory"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*abstention*");
    }

    [Fact]
    public void AnUnknownTypeIsRejectedAndListsTheKnownOnes()
    {
        // A typo must not silently sample everything. Listing the known types is what separates
        // "misspelled" from "not present in this dataset" for the reader.
        var act = () => LongMemEvalMemoryTypeSelection.TaskTypesFor(["epsiodic"]);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("episodic");
    }

    [Fact]
    public void OneBadTypeRejectsTheWholeRequest()
    {
        // Partial acceptance is the dangerous middle: the run would proceed, sample only the valid
        // types, and report itself as covering both.
        var act = () => LongMemEvalMemoryTypeSelection.TaskTypesFor(["episodic", "procedural"]);

        act.Should().Throw<ArgumentException>();
    }

    // ── consistency with the reporting side ──────────────────────────────

    [Fact]
    public void EverySelectedLabelReportsTheTypeItWasSelectedFor()
    {
        // Selection and reporting must come from one taxonomy. If they ever diverged, a run would
        // sample one set of labels and compute per-type accuracy from another -- and both halves would
        // look internally correct, which is why this is asserted rather than assumed.
        var map = LongMemEvalMemoryTypeMap.Default;

        foreach (var type in new[] { "semantic", "episodic", "temporal" })
        {
            var labels = LongMemEvalMemoryTypeSelection.TaskTypesFor([type]);
            labels.Should().NotBeNullOrEmpty();
            foreach (var label in labels!)
                map.ForQuestion(label, isAbstention: false).Should().Contain(type,
                    $"'{label}' was selected for '{type}', so it must report as '{type}'");
        }
    }

    [Fact]
    public void EveryTaskLabelInTheDatasetIsReachableFromSomeType()
    {
        // A label no type selects is a question that can never appear in a typed run: it would vanish
        // from typed measurement entirely while still counting in the aggregate.
        var map = LongMemEvalMemoryTypeMap.Default;
        var reachable = new[] { "semantic", "episodic", "temporal" }
            .SelectMany(type => LongMemEvalMemoryTypeSelection.TaskTypesFor([type]) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        map.TaskTypes.Keys.Should().BeSubsetOf(reachable);
    }
}
