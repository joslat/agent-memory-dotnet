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
        var cypher = FactQueries.SearchByCanonicalPredicates;

        cypher.Should().Contain("f.predicate_key IN $predicateKeys");
        cypher.Should().NotContain("f.predicate IN");
    }

    [Fact]
    public void ExpansionIsOwnerScoped()
    {
        // A relation query that crosses owners would leak one user's facts into another's context.
        FactQueries.SearchByCanonicalPredicates.Should().Contain("owner_key");
    }

    [Fact]
    public void ExpansionIsBounded()
    {
        // Unbounded completeness on a ~962-item graph is a denial of service on the context budget.
        FactQueries.SearchByCanonicalPredicates.Should().Contain("LIMIT $limit");
    }

    [Fact]
    public void ExpansionReturnsFactsInADeterministicOrder()
    {
        // Two runs of one question must select the same facts, or the comparison is unrepeatable.
        FactQueries.SearchByCanonicalPredicates.Should().Contain("ORDER BY");
    }
}
