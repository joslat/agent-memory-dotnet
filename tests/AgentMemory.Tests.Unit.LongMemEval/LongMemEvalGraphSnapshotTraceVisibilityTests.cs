using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Making the probe able to see procedural memory at all (PLAN 6.5).
/// </summary>
/// <remarks>
/// <para>
/// <c>LongMemEvalGraphProbe</c> counted Entity, Fact, Preference and <c>RELATED_TO</c>. It was
/// <b>label-blind to <c>:ReasoningTrace</c></b>, which meant "the corpus contains no traces" and "the
/// probe cannot see traces" produced byte-identical output — and Phase 7's procedural work was about
/// to be measured against a graph nobody had confirmed held anything to measure.
/// </para>
/// </remarks>
public sealed class LongMemEvalGraphSnapshotTraceVisibilityTests
{
    private static LongMemEvalGraphSnapshot Snapshot(int? traces = null, int? procedures = null) =>
        new(Entities: 10, Facts: 20, Preferences: 5, Relationships: 3,
            RelationshipsWithProvenance: 3, LearnedItems: 35, LearnedItemsWithProvenance: 35,
            ProvenanceEdges: 40, SourceMessages: 12,
            ReasoningTraces: traces, Procedures: procedures);

    [Fact]
    public void TotalLearnedStillExcludesTraces()
    {
        // Comparability. This number appears in every measurement sealed before the probe could see
        // traces; widening its definition would make every prior build appear to have grown for a
        // reason unrelated to what was extracted.
        Snapshot(traces: 7).TotalLearned.Should().Be(38);
    }

    [Fact]
    public void TracesAreCountedSeparately() =>
        Snapshot(traces: 7).TotalIncludingTraces.Should().Be(45);

    [Fact]
    public void NotMeasuredIsNotTheSameAsMeasuredZero()
    {
        // THE distinction, and the reason the counts are nullable. A manifest written before this
        // change has no such field, and a non-nullable int would deserialize it to 0 -- reproducing in
        // the recorded data the exact ambiguity 6.5 exists to remove.
        var legacy = Snapshot(traces: null);
        var measured = Snapshot(traces: 0);

        legacy.TracesMeasured.Should().BeFalse();
        legacy.HasNoTraces.Should().BeFalse("a probe that never looked has not found the corpus empty");

        measured.TracesMeasured.Should().BeTrue();
        measured.HasNoTraces.Should().BeTrue();
    }

    [Fact]
    public void AnUnmeasuredTraceCountDoesNotInflateTheTotal() =>
        Snapshot(traces: null).TotalIncludingTraces.Should().Be(38);

    [Fact]
    public void ProceduresAreCountedApartFromTracesInGeneral()
    {
        // Phase 7 promotes procedures specifically; a total trace count that lumped episodes in with
        // them would report a corpus as procedure-bearing when it holds none.
        Snapshot(traces: 7, procedures: 2).Procedures.Should().Be(2);
    }
}
