using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// When an owner-scoped vector search comes back <b>short but not empty</b> (PLAN 2.11).
/// </summary>
/// <remarks>
/// <para>
/// Neo4j's vector index is global, so an owner filter is a POST-filter on a top-K drawn from every
/// tenant. Measured on a 50-owner corpus, a mean of <b>7 of 60</b> candidates reached the querying
/// owner. Only a totally empty result triggered a rescue, on the argument that "a short-but-non-empty
/// result still answers the question".
/// </para>
/// <para>
/// <b>That argument has a measured counter-example.</b> Question <c>5d3d2817</c> returned 2 facts from
/// a 710-fact graph with the gold answer present at coverage 1.00, and both arms answered it wrongly.
/// The claim is true for a small tenant and false for a crowded one, and the returned count alone
/// cannot separate them.
/// </para>
/// <para>
/// The rescue is the owner-bounded <i>scan</i>, not another widening — widening is a second draw on
/// the same global index, and a tenant losing to 50 neighbours at top-60 usually loses again at
/// top-480. The scan's cost scales with one owner's rows, so the small tenant the original argument
/// protected pays less for it than for a wider index query.
/// </para>
/// </remarks>
public sealed class ShortOwnerResultRescueTests
{
    [Fact]
    public void AShortScopedResultIsRescued()
    {
        // The measured case: 2 rows of a requested 10.
        OwnerVectorOverFetch.ShouldRescueShortResult(returned: 2, limit: 10, hasOwner: true)
            .Should().BeTrue();
    }

    [Fact]
    public void AFullResultIsNotRescued()
    {
        // Nothing was crowded out: the limit was met, so there is no shortfall to explain.
        OwnerVectorOverFetch.ShouldRescueShortResult(returned: 10, limit: 10, hasOwner: true)
            .Should().BeFalse();
    }

    [Fact]
    public void AnEmptyResultIsLeftToTheEscalationPath()
    {
        // Zero already has an owner: ShouldEscalate tries one widened index query first and only then
        // falls back to the scan. Claiming it here too would run the scan twice on the same search.
        OwnerVectorOverFetch.ShouldRescueShortResult(returned: 0, limit: 10, hasOwner: true)
            .Should().BeFalse();
        OwnerVectorOverFetch.ShouldEscalate(returned: 0, hasOwner: true).Should().BeTrue();
    }

    [Fact]
    public void AnUnscopedSearchIsNeverRescued()
    {
        // With no owner filter there is no post-filter and therefore no crowding: a short result means
        // the corpus genuinely held that little above the floor, and a scan returns the same rows more
        // slowly.
        OwnerVectorOverFetch.ShouldRescueShortResult(returned: 2, limit: 10, hasOwner: false)
            .Should().BeFalse();
    }

    [Fact]
    public void TheTwoRescuePathsAreMutuallyExclusiveAtEveryCount()
    {
        // A count that satisfied both would run the widening AND the scan for one search. Asserted
        // across the whole range rather than at the boundary, because the two predicates are written
        // independently and nothing else forces them apart.
        for (var returned = 0; returned <= 12; returned++)
        {
            var escalate = OwnerVectorOverFetch.ShouldEscalate(returned, hasOwner: true);
            var rescue = OwnerVectorOverFetch.ShouldRescueShortResult(returned, limit: 10, hasOwner: true);

            (escalate && rescue).Should().BeFalse($"returned={returned} must not trigger both paths");
        }
    }

    [Fact]
    public void EveryShortfallCounts_ThereIsNoRatioThreshold()
    {
        // A fraction of the limit would need a cutoff nobody can justify -- is 4 of 10 starved? 6? --
        // and the honest answer is that any shortfall might be crowding, because the index gave the
        // owner whatever the neighbours left. The scan is bounded by the owner's rows either way, so
        // the cost question is answered by its shape rather than by guessing a threshold.
        for (var returned = 1; returned < 10; returned++)
        {
            OwnerVectorOverFetch.ShouldRescueShortResult(returned, limit: 10, hasOwner: true)
                .Should().BeTrue($"returned={returned} of 10 is short");
        }
    }

    [Fact]
    public void TheRescueIsOffByDefault()
    {
        // It trades latency for recall on every short result, and every recorded measurement was taken
        // without it, so enabling it is a stated decision rather than an inherited one.
        new Abstractions.Options.MemoryOptions().RescueShortOwnerResults.Should().BeFalse();
    }

    [Fact]
    public void EscalationStillOnlyFiresOnEmpty()
    {
        // The byte-identical guarantee: 2.11 adds a path, it does not widen the existing one. A host
        // that leaves the option off gets exactly today's behaviour.
        OwnerVectorOverFetch.ShouldEscalate(returned: 1, hasOwner: true).Should().BeFalse();
        OwnerVectorOverFetch.ShouldEscalate(returned: 0, hasOwner: false).Should().BeFalse();
    }
}
