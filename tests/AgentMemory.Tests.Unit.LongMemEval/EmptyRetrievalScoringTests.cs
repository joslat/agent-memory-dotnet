using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// When retrieval returns nothing, decide whether that is a measurable result or a broken run.
/// </summary>
/// <remarks>
/// The adapter refuses to score an empty retrieval — "refusing to manufacture a score" — which is
/// right when an empty retrieval means the harness malfunctioned. But the n=50 rebuild of
/// 2026-08-10 produced a case where it is wrong: question <c>32260d93</c> retrieved nothing from a
/// graph the read-back had already proven held <b>504 facts, 346 entities and 1,336 learned items
/// with complete provenance</b>, and whose gold evidence was demonstrably learned. Nothing
/// malfunctioned. The memory system genuinely returned nothing, and the honest score for that is
/// <i>wrong</i>, not <i>unmeasurable</i>.
/// <para>
/// Throwing cost the entire run: the structured arm produced 49 answers to hybrid's 50, so the pair
/// was unpaired and the whole comparison was rejected — discarding 83 minutes of extraction over the
/// one question that most sharply discriminates the arms.
/// </para>
/// <para>
/// This is a <b>refinement</b> of the guard, not a weakening. The graph read-back runs earlier and
/// already throws when it cannot prove the memory is populated, so "populated with complete
/// provenance" is the exact evidence that separates the two cases. Without that proof — the probe
/// disabled, so the snapshot is null — the cases are indistinguishable and the guard must still fire.
/// </para>
/// </remarks>
public sealed class EmptyRetrievalScoringTests
{
    private static LongMemEvalGraphSnapshot Snapshot(
        int entities = 346, int facts = 504, int preferences = 191, int relationships = 295,
        int? learnedWithProvenance = null, int? relationshipsWithProvenance = null)
    {
        var learned = entities + facts + preferences;
        return new LongMemEvalGraphSnapshot(
            entities, facts, preferences, relationships,
            relationshipsWithProvenance ?? relationships,
            learned,
            learnedWithProvenance ?? learned,
            ProvenanceEdges: 12932,
            SourceMessages: 518);
    }

    [Fact]
    public void AProvenPopulatedGraphMakesAnEmptyRetrievalScorable()
    {
        // The real case: retrieval failed, the graph did not.
        AgentMemoryLongMemEvalAdapter.CanScoreEmptyRetrieval(Snapshot()).Should().BeTrue();
    }

    [Fact]
    public void NoSnapshotMeansNoProofSoTheGuardStillFires()
    {
        // The probe is optional. With it disabled we cannot tell a retrieval failure from a broken
        // preparation, and guessing is exactly what the guard exists to prevent.
        AgentMemoryLongMemEvalAdapter.CanScoreEmptyRetrieval(null).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyGraphIsAPreparationFailureAndStillThrows()
    {
        AgentMemoryLongMemEvalAdapter
            .CanScoreEmptyRetrieval(Snapshot(entities: 0, facts: 0, preferences: 0, relationships: 0))
            .Should().BeFalse();
    }

    [Fact]
    public void IncompleteProvenanceIsStillAPreparationFailure()
    {
        // A populated graph whose provenance is incomplete is not proof of a sound build, so an
        // empty retrieval against it remains unmeasurable rather than merely wrong.
        AgentMemoryLongMemEvalAdapter
            .CanScoreEmptyRetrieval(Snapshot(learnedWithProvenance: 3))
            .Should().BeFalse();

        AgentMemoryLongMemEvalAdapter
            .CanScoreEmptyRetrieval(Snapshot(relationshipsWithProvenance: 2))
            .Should().BeFalse();
    }
}

/// <summary>
/// The validator enforces the same rule independently, so it needs the same distinction.
/// </summary>
/// <remarks>
/// This is the "check whether a second implementation exists" rule paying for itself. Fixing only
/// the adapter would have produced 50 answers and then had the run rejected anyway, by
/// <c>LongMemEvalRunValidator</c>'s own <c>ItemsRetrieved == 0</c> check and by its failed-stage
/// sweep over any status that is not <c>completed</c> — costing a second 12-minute run to discover.
/// </remarks>
public sealed class ScoredEmptyRetrievalValidationTests
{
    [Fact]
    public void AScoredEmptyRetrievalIsRecognisedByTheSamePredicateTheAdapterUses()
    {
        // One source of truth: the validator must not re-derive the rule from its own reasoning.
        var populated = new LongMemEvalGraphSnapshot(
            346, 504, 191, 295, 295, 1041, 1041, 12932, 518);

        AgentMemoryLongMemEvalAdapter.CanScoreEmptyRetrieval(populated).Should().BeTrue();
        LongMemEvalRunValidator.IsScoredEmptyRetrieval("retrieval-empty", populated).Should().BeTrue();
    }

    [Fact]
    public void OnlyTheRetrievalEmptyStatusQualifies()
    {
        var populated = new LongMemEvalGraphSnapshot(
            346, 504, 191, 295, 295, 1041, 1041, 12932, 518);

        // A storage error against a populated graph is still a broken run, not a measured zero.
        LongMemEvalRunValidator.IsScoredEmptyRetrieval("storage-error", populated).Should().BeFalse();
        LongMemEvalRunValidator.IsScoredEmptyRetrieval("completed", populated).Should().BeFalse();
    }

    [Fact]
    public void WithoutGraphProofItIsStillARejection()
    {
        LongMemEvalRunValidator.IsScoredEmptyRetrieval("retrieval-empty", null).Should().BeFalse();
    }
}
