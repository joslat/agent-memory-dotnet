using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Owner-scoped vector search is a POST-filter on a global top-K, so the over-fetch decides how much
/// of the budget the asking owner actually gets.
/// </summary>
/// <remarks>
/// Measured against the sealed n=50 base on 2026-08-10: 26,236 facts across 50 owners, over-fetch
/// <c>max(limit*5, limit+50)</c> = 60 for <c>MaxFacts = 10</c>. Probing with each owner's own message,
/// the owner's own facts inside that global top-60 came to a <b>mean of 7, minimum 1</b> — 88% of the
/// budget went to other tenants — and one real question received <b>zero</b> from a graph holding 504
/// of its own facts, every one of them live, embedded, and scoring above the similarity floor.
/// <para>
/// The isolation is correct throughout: no foreign row is ever returned. What degrades is <i>recall</i>,
/// silently, as neighbouring tenants are added — and the over-fetch is a fixed heuristic that does not
/// scale with tenant count.
/// </para>
/// <para>
/// Escalation is deliberately restricted to the <b>empty</b> result. A short-but-non-empty result still
/// answers the question, whereas zero is total failure; and escalating on "short" would tax every
/// small tenant with extra queries forever, since an owner holding three facts can never fill a
/// ten-row limit. One extra query, only when the first pass found nothing.
/// </para>
/// </remarks>
public sealed class OwnerVectorOverFetchTests
{
    [Fact]
    public void UnscopedSearchDoesNotOverFetch()
    {
        // With no owner filter there is nothing to be starved by, so the budget is the limit.
        OwnerVectorOverFetch.InitialTopK(10, hasOwner: false).Should().Be(10);
    }

    [Fact]
    public void ScopedSearchOverFetchesTheHistoricalAmount()
    {
        // Unchanged from the six hand-copied call sites this replaces: max(limit*5, limit+50).
        OwnerVectorOverFetch.InitialTopK(10, hasOwner: true).Should().Be(60);
        OwnerVectorOverFetch.InitialTopK(100, hasOwner: true).Should().Be(500);
        OwnerVectorOverFetch.InitialTopK(1, hasOwner: true).Should().Be(51);
    }

    [Fact]
    public void AnEmptyScopedResultEscalates()
    {
        OwnerVectorOverFetch.ShouldEscalate(returned: 0, hasOwner: true).Should().BeTrue();
    }

    [Fact]
    public void ANonEmptyResultDoesNotEscalate()
    {
        // The load-bearing restraint: one row is an answer, and escalating on "short" would make
        // every small tenant pay an extra query on every recall, permanently.
        OwnerVectorOverFetch.ShouldEscalate(returned: 1, hasOwner: true).Should().BeFalse();
        OwnerVectorOverFetch.ShouldEscalate(returned: 7, hasOwner: true).Should().BeFalse();
    }

    [Fact]
    public void AnUnscopedEmptyResultDoesNotEscalate()
    {
        // Without an owner filter, empty means the corpus genuinely had nothing above the floor.
        // Re-querying wider would return the same nothing, more slowly.
        OwnerVectorOverFetch.ShouldEscalate(returned: 0, hasOwner: false).Should().BeFalse();
    }

    [Fact]
    public void EscalationWidensSubstantiallyButStaysBounded()
    {
        var escalated = OwnerVectorOverFetch.EscalatedTopK(60);

        escalated.Should().BeGreaterThan(60, "a retry at the same width returns the same rows");
        escalated.Should().BeLessThanOrEqualTo(OwnerVectorOverFetch.MaxTopK);
    }

    [Fact]
    public void EscalationIsCappedSoItCannotDegradeIntoAFullScan()
    {
        OwnerVectorOverFetch.EscalatedTopK(OwnerVectorOverFetch.MaxTopK)
            .Should().Be(OwnerVectorOverFetch.MaxTopK);
        OwnerVectorOverFetch.EscalatedTopK(int.MaxValue / 2)
            .Should().Be(OwnerVectorOverFetch.MaxTopK);
    }
}
