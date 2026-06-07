using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Guard tests for the non-vector read queries that became owner-conditional (R1, unscoped-reads
/// hardening): EntityQueries.GetByType / FindSimilarByEmbedding / SearchByNameFiltered and
/// FactQueries.FindByTriple. The owner predicate must appear only when a scope is supplied, and must
/// include shared/global rows (owner_id IS NULL) exactly when the scope opts in.
/// </summary>
public sealed class ScopedNonVectorReadQueryTests
{
    // ── EntityQueries.GetByType (entity-resolution write-path; the real leak) ──

    [Fact]
    public void GetByType_Unscoped_HasNoOwnerPredicate()
    {
        var cypher = EntityQueries.GetByType(hasOwnerFilter: false, includeShared: true);
        cypher.Should().Contain("(e:Entity {type: $type})").And.NotContain("owner_id");
    }

    [Fact]
    public void GetByType_ScopedIncludeShared_MatchesOwnerOrNull()
    {
        EntityQueries.GetByType(hasOwnerFilter: true, includeShared: true)
            .Should().Contain("(e.owner_id = $ownerId OR e.owner_id IS NULL)");
    }

    [Fact]
    public void GetByType_ScopedExcludeShared_MatchesOwnerOnly()
    {
        var cypher = EntityQueries.GetByType(hasOwnerFilter: true, includeShared: false);
        cypher.Should().Contain("e.owner_id = $ownerId").And.NotContain("IS NULL");
    }

    // ── EntityQueries.FindSimilarByEmbedding (dedup) ──

    [Fact]
    public void FindSimilarByEmbedding_Unscoped_HasNoOwnerPredicate()
    {
        var cypher = EntityQueries.FindSimilarByEmbedding(hasOwnerFilter: false, includeShared: true);
        cypher.Should().Contain("db.index.vector.queryNodes('entity_embedding_idx'").And.NotContain("owner_id");
    }

    [Fact]
    public void FindSimilarByEmbedding_ScopedIncludeShared_MatchesOwnerOrNull()
    {
        EntityQueries.FindSimilarByEmbedding(hasOwnerFilter: true, includeShared: true)
            .Should().Contain("(node.owner_id = $ownerId OR node.owner_id IS NULL)");
    }

    [Fact]
    public void FindSimilarByEmbedding_ScopedExcludeShared_MatchesOwnerOnly()
    {
        var cypher = EntityQueries.FindSimilarByEmbedding(hasOwnerFilter: true, includeShared: false);
        cypher.Should().Contain("node.owner_id = $ownerId").And.NotContain("IS NULL");
    }

    // ── EntityQueries.SearchByNameFiltered (name search) ──

    [Fact]
    public void SearchByNameFiltered_Unscoped_HasNoOwnerPredicate_AndParenthesizesNameOr()
    {
        var cypher = EntityQueries.SearchByNameFiltered(type: null, hasOwnerFilter: false, includeShared: true);
        cypher.Should().NotContain("owner_id");
        // The name/canonical OR must be parenthesized so type/owner predicates AND correctly across it.
        cypher.Should().Contain("(toLower(e.name) CONTAINS toLower($name) OR toLower(e.canonical_name) CONTAINS toLower($name))");
    }

    [Fact]
    public void SearchByNameFiltered_ScopedIncludeShared_MatchesOwnerOrNull()
    {
        EntityQueries.SearchByNameFiltered(type: "Person", hasOwnerFilter: true, includeShared: true)
            .Should().Contain("(e.owner_id = $ownerId OR e.owner_id IS NULL)");
    }

    // ── FactQueries.FindByTriple ──

    [Fact]
    public void FindByTriple_Unscoped_HasNoOwnerPredicate()
    {
        var cypher = FactQueries.FindByTriple(hasOwnerFilter: false, includeShared: true);
        cypher.Should().Contain("toLower(f.subject) = toLower($subject)").And.NotContain("owner_id");
    }

    [Fact]
    public void FindByTriple_ScopedIncludeShared_MatchesOwnerOrNull()
    {
        FactQueries.FindByTriple(hasOwnerFilter: true, includeShared: true)
            .Should().Contain("(f.owner_id = $ownerId OR f.owner_id IS NULL)");
    }

    [Fact]
    public void FindByTriple_ScopedExcludeShared_MatchesOwnerOnly()
    {
        var cypher = FactQueries.FindByTriple(hasOwnerFilter: true, includeShared: false);
        cypher.Should().Contain("f.owner_id = $ownerId").And.NotContain("IS NULL");
    }
}
