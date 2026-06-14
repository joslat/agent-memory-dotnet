using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Guard tests that lock the multi-user isolation semantics (R1) into the long-term vector-search
/// Cypher, plus the opt-in D1 recency re-rank blend. The owner predicate must appear ONLY when a scope
/// is supplied, and the shared/global rows (owner_id IS NULL) must be included exactly when the scope
/// opts in. Each query over-fetches topK candidates and LIMITs after the WHERE so an owner filter is
/// never starved by foreign rows. With <c>recencyRerank: false</c> the query is byte-for-byte today's
/// semantic-only ranking; with it true, the clamped ACT-R retention score is blended into the order key.
/// </summary>
public sealed class ScopedVectorSearchQueryTests
{
    public static IEnumerable<object[]> AllSearchByVector()
    {
        yield return new object[] { "Fact", (Func<bool, bool, int, bool, string>)FactQueries.SearchByVector, "fact_embedding_idx" };
        yield return new object[] { "Entity", (Func<bool, bool, int, bool, string>)EntityQueries.SearchByVector, "entity_embedding_idx" };
        yield return new object[] { "Preference", (Func<bool, bool, int, bool, string>)PreferenceQueries.SearchByVector, "preference_embedding_idx" };
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void Unscoped_HasNoOwnerPredicate(string label, Func<bool, bool, int, bool, string> build, string indexName)
    {
        var cypher = build(/*hasOwnerFilter*/ false, /*includeShared*/ true, /*topK*/ 10, /*recencyRerank*/ false);

        cypher.Should().NotContain("owner_id", $"{label} unscoped search must not filter by owner");
        cypher.Should().Contain(indexName);
        cypher.Should().Contain("LIMIT $limit");
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void ScopedIncludeShared_FiltersOwnerOrNull(string label, Func<bool, bool, int, bool, string> build, string indexName)
    {
        var cypher = build(/*hasOwnerFilter*/ true, /*includeShared*/ true, /*topK*/ 50, /*recencyRerank*/ false);

        cypher.Should().Contain("(node.owner_id = $ownerId OR node.owner_id IS NULL)",
            $"{label} scoped+shared search must match the owner's rows OR shared/global rows");
        cypher.Should().Contain(indexName);
        cypher.Should().Contain("LIMIT $limit");
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void ScopedExcludeShared_FiltersOwnerOnly(string label, Func<bool, bool, int, bool, string> build, string indexName)
    {
        var cypher = build(/*hasOwnerFilter*/ true, /*includeShared*/ false, /*topK*/ 50, /*recencyRerank*/ false);

        cypher.Should().Contain(indexName, $"{label} searches its own vector index");
        cypher.Should().Contain("node.owner_id = $ownerId");
        cypher.Should().NotContain("owner_id IS NULL",
            $"{label} owner-only search must exclude shared/global (owner_id IS NULL) rows");
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void LiveRecall_ExcludesInvalidatedNodes(string label, Func<bool, bool, int, bool, string> build, string indexName)
    {
        // D5: live recall must drop soft-invalidated nodes (transaction clock). No-op for today's data
        // (no node has invalidated_at set) but it is what makes a soft-invalidate disappear from recall.
        foreach (var (scoped, shared, rerank) in new[] { (false, true, false), (true, true, false), (true, false, true) })
        {
            var cypher = build(scoped, shared, 50, rerank);
            cypher.Should().Contain(indexName, $"{label} searches its own vector index");
            cypher.Should().Contain("node.invalidated_at IS NULL",
                $"{label} live recall must exclude invalidated nodes (scoped={scoped}, rerank={rerank})");
        }
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void OverFetch_TopKAppearsInVectorQuery(string label, Func<bool, bool, int, bool, string> build, string indexName)
    {
        // The over-fetch count (topK) is the candidate set the vector index returns BEFORE the owner
        // WHERE + LIMIT $limit. It must be embedded in the db.index.vector.queryNodes call.
        var cypher = build(true, true, 123, false);

        cypher.Should().Contain($"db.index.vector.queryNodes('{indexName}', 123",
            $"{label} must over-fetch topK candidates from its vector index before filtering");
    }

    // ── D1 recency re-rank (opt-in) ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void RecencyRerankOff_IsSemanticOnly(string label, Func<bool, bool, int, bool, string> build, string indexName)
    {
        var cypher = build(false, true, 10, /*recencyRerank*/ false);

        cypher.Should().Contain(indexName, $"{label} searches its own vector index");
        cypher.Should().NotContain("$tmpWeight", $"{label} off-path must not blend the recency term");
        cypher.Should().NotContain("sTmp");
        cypher.Should().NotContain("$lambda");
        cypher.Should().Contain("RETURN node, score");
        cypher.Should().Contain("ORDER BY score DESC");
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void RecencyRerankOn_BlendsClampedRetentionIntoScore(string label, Func<bool, bool, int, bool, string> build, string indexName)
    {
        var cypher = build(false, true, 10, /*recencyRerank*/ true);

        cypher.Should().Contain("exp(-$lambda * daysSince)", $"{label} must reuse the ACT-R decay curve");
        cypher.Should().Contain("$boostFactor * COALESCE(node.access_count, 0)");
        cypher.Should().Contain("AS sTmp");
        cypher.Should().Contain("((1.0 - $tmpWeight) * score + $tmpWeight * sTmp) AS score",
            $"{label} must blend the semantic and recency scores convexly");
        cypher.Should().Contain("ORDER BY score DESC");
        cypher.Should().Contain("LIMIT $limit");
        cypher.Should().Contain(indexName);
    }

    [Theory]
    [MemberData(nameof(AllSearchByVector))]
    public void RecencyRerankOn_OwnerOnly_StillHasNoOwnerIsNull(string label, Func<bool, bool, int, bool, string> build, string indexName)
    {
        // The blend uses COALESCE, never `IS NULL`, so the owner-only variant must not introduce an
        // `owner_id IS NULL` clause (which would leak shared/global rows into a private recall).
        var cypher = build(/*hasOwnerFilter*/ true, /*includeShared*/ false, /*topK*/ 50, /*recencyRerank*/ true);

        cypher.Should().Contain(indexName, $"{label} searches its own vector index");
        cypher.Should().Contain("node.owner_id = $ownerId");
        cypher.Should().NotContain("owner_id IS NULL",
            $"{label} owner-only + recency-rerank must exclude shared/global rows");
    }
}
