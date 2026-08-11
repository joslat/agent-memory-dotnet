using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// J3.1. The question's own relation must not be starved by unrelated predicates.
/// </summary>
/// <remarks>
/// Expansion issues ONE query with a single shared <c>LIMIT</c> over the question's resolved
/// relations <b>union</b> the canonical predicate of every top-K vector hit, ordered globally by
/// <c>confidence DESC</c>. High-confidence facts from unrelated predicates therefore consume the
/// budget before the relation the question actually named is exhausted.
/// <para>
/// Measured, not hypothesised: question <c>a9f6b44c</c> holds 49 facts under <c>planned</c>/<c>plans</c>,
/// had a 60-row budget, and received <b>22</b> — the other 38 slots went elsewhere. That defeats the
/// entire guarantee expansion exists for, which is that a relation arrives <b>whole</b>; top-K is a
/// relevance cutoff and already gives no completeness.
/// </para>
/// <para>
/// The fix orders the question's keys ahead of the borrowed ones. It changes nothing when the budget
/// is not binding, which is why the ordering is a tiebreak rather than a filter.
/// </para>
/// </remarks>
public sealed class RelationPriorityExpansionTests
{
    [Fact]
    public void PriorityKeysAreOrderedAheadOfBorrowedPredicates()
    {
        var cypher = FactQueries.SearchByCanonicalPredicates(
            hasOwnerFilter: true, includeShared: true, hasPriorityKeys: true);

        // The question's own relation is exhausted before any borrowed predicate takes a slot.
        cypher.Should().Contain("$priorityKeys");
        cypher.Should().MatchRegex(@"ORDER BY[\s\S]*priorityKeys[\s\S]*confidence DESC");
    }

    [Fact]
    public void WithoutPriorityKeysTheQueryIsByteForByteUnCHANGED()
    {
        // The load-bearing compatibility property. A caller that supplies no priority keys - every
        // existing caller - must get exactly the query it got before, or this is a silent retrieval
        // change riding along with a fix.
        var withoutFlag = FactQueries.SearchByCanonicalPredicates(
            hasOwnerFilter: true, includeShared: true);
        var explicitlyNone = FactQueries.SearchByCanonicalPredicates(
            hasOwnerFilter: true, includeShared: true, hasPriorityKeys: false);

        explicitlyNone.Should().Be(withoutFlag);
        withoutFlag.Should().NotContain("priorityKeys");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void OwnerScopingSurvivesThePriorityOrdering(bool hasOwner, bool includeShared)
    {
        // Ordering must never widen what is visible. The owner predicate has to be identical with
        // and without prioritisation, because an isolation regression hidden inside a ranking change
        // is the worst possible way to ship one.
        var plain = FactQueries.SearchByCanonicalPredicates(hasOwner, includeShared);
        var prioritised = FactQueries.SearchByCanonicalPredicates(hasOwner, includeShared, true);

        static string WhereOf(string cypher) =>
            cypher[cypher.IndexOf("WHERE", StringComparison.Ordinal)
                   ..cypher.IndexOf("RETURN", StringComparison.Ordinal)];

        WhereOf(prioritised).Should().Be(WhereOf(plain));
    }
}
