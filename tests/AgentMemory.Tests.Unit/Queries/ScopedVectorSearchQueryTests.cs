using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Guard tests that lock the multi-user isolation semantics (R1) into the long-term vector-search
/// Cypher. The owner predicate must appear ONLY when a scope is supplied, and the shared/global rows
/// (owner_id IS NULL) must be included exactly when the scope opts in. Each query over-fetches topK
/// candidates and LIMITs after the WHERE so an owner filter is never starved by foreign rows.
/// </summary>
public sealed class ScopedVectorSearchQueryTests
{
    public static IEnumerable<object[]> AllSearchByVector()
    {
        yield return new object[] { "Fact", (Func<bool, bool, int, string>)FactQueries.SearchByVector, "fact_embedding_idx" };
        yield return new object[] { "Entity", (Func<bool, bool, int, string>)EntityQueries.SearchByVector, "entity_embedding_idx" };
        yield return new object[] { "Preference", (Func<bool, bool, int, string>)PreferenceQueries.SearchByVector, "preference_embedding_idx" };
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void Unscoped_HasNoOwnerPredicate(string label, Func<bool, bool, int, string> build, string indexName)
    {
        var cypher = build(/*hasOwnerFilter*/ false, /*includeShared*/ true, /*topK*/ 10);

        cypher.Should().NotContain("owner_id", $"{label} unscoped search must not filter by owner");
        cypher.Should().Contain(indexName);
        cypher.Should().Contain("LIMIT $limit");
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void ScopedIncludeShared_FiltersOwnerOrNull(string label, Func<bool, bool, int, string> build, string indexName)
    {
        var cypher = build(/*hasOwnerFilter*/ true, /*includeShared*/ true, /*topK*/ 50);

        cypher.Should().Contain("(node.owner_id = $ownerId OR node.owner_id IS NULL)",
            $"{label} scoped+shared search must match the owner's rows OR shared/global rows");
        cypher.Should().Contain(indexName);
        cypher.Should().Contain("LIMIT $limit");
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void ScopedExcludeShared_FiltersOwnerOnly(string label, Func<bool, bool, int, string> build, string indexName)
    {
        var cypher = build(/*hasOwnerFilter*/ true, /*includeShared*/ false, /*topK*/ 50);

        cypher.Should().Contain("node.owner_id = $ownerId");
        cypher.Should().NotContain("IS NULL",
            $"{label} owner-only search must exclude shared/global (owner_id IS NULL) rows");
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void OverFetch_TopKAppearsInVectorQuery(string label, Func<bool, bool, int, string> build, string indexName)
    {
        // The over-fetch count (topK) is the candidate set the vector index returns BEFORE the owner
        // WHERE + LIMIT $limit. It must be embedded in the db.index.vector.queryNodes call.
        var cypher = build(true, true, 123);

        cypher.Should().Contain($"db.index.vector.queryNodes('{indexName}', 123",
            $"{label} must over-fetch topK candidates from its vector index before filtering");
    }
}
