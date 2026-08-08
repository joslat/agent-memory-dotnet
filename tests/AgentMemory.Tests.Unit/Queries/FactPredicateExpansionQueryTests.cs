using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// G3B.13. Counting questions need <b>completeness</b> of a relation, which top-K similarity cannot
/// provide — it is a relevance cutoff. Measured: "how many babies were born" needs all five births,
/// all five are in the graph, and recall returned a similarity-truncated subset of a 962-item pool.
/// Expansion retrieves every fact sharing a canonical predicate so the relation arrives whole.
/// </summary>
public sealed class FactPredicateExpansionQueryTests
{
    [Fact]
    public void ExpansionMatchesTheCanonicalPredicateNeverTheRawText()
    {
        // Matching raw text would reinstate exactly the fragmentation canonical identity removed:
        // "were_born_in" and "were born in" would once again fail to find each other.
        var cypher = FactQueries.SearchByCanonicalPredicates(hasOwnerFilter: true, includeShared: true);

        cypher.Should().Contain("f.predicate_key IN $predicateKeys");
        cypher.Should().NotContain("f.predicate IN");
    }

    [Fact]
    public void ExpansionIsOwnerScoped()
    {
        // A relation query that crosses owners would leak one user's facts into another's context.
        // Scoping is by owner_id, matching every other fact read; the original owner_key form was
        // the hard-coded version the audit found wrong.
        FactQueries.SearchByCanonicalPredicates(hasOwnerFilter: true, includeShared: true).Should()
            .Contain("f.owner_id = $ownerId");
    }

    [Fact]
    public void ExpansionIsBounded()
    {
        // Unbounded completeness on a ~962-item graph is a denial of service on the context budget.
        FactQueries.SearchByCanonicalPredicates(hasOwnerFilter: true, includeShared: true).Should().Contain("LIMIT $limit");
    }

    [Fact]
    public void ExpansionReturnsFactsInADeterministicOrder()
    {
        // Two runs of one question must select the same facts, or the comparison is unrepeatable.
        FactQueries.SearchByCanonicalPredicates(hasOwnerFilter: true, includeShared: true).Should().Contain("ORDER BY");
    }

    [Fact]
    public void SharedFactsAreIncludedWhenTheScopeAllowsThem()
    {
        // Audit finding: the first version matched only the owner's own bucket, so a shared fact was
        // silently absent and the "relation whole" guarantee was false.
        FactQueries.SearchByCanonicalPredicates(hasOwnerFilter: true, includeShared: true)
            .Should().Contain("f.owner_id IS NULL");
    }

    [Fact]
    public void SharedFactsAreExcludedWhenTheScopeForbidsThem()
    {
        FactQueries.SearchByCanonicalPredicates(hasOwnerFilter: true, includeShared: false)
            .Should().NotContain("f.owner_id IS NULL");
    }

    [Fact]
    public void NoOwnerFilterMeansNoOwnerPredicateAtAll()
    {
        // Audit finding: a null-owner scope was coerced to the shared bucket, so expansion returned
        // nothing exactly where top-K returned everything.
        var cypher = FactQueries.SearchByCanonicalPredicates(hasOwnerFilter: false, includeShared: true);

        cypher.Should().NotContain("owner_id");
        cypher.Should().NotContain("owner_key");
    }
}
