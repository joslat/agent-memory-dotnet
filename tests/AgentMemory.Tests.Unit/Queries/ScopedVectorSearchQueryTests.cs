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
        // Fact's SearchByVector gained a valid-time parameter; adapt it to the shared shape so this
        // scoping contract still covers all three. The gate is off here on purpose -- these tests are
        // about OWNER scoping, and ValidTimeGateQueryTests covers the new clause.
        yield return new object[]
        {
            "Fact",
            (Func<bool, bool, int, bool, string>)((owner, shared, topK, rerank) =>
                FactQueries.SearchByVector(owner, shared, topK, rerank)),
            "fact_embedding_idx",
        };
        yield return new object[]
        {
            "Entity",
            (Func<bool, bool, int, bool, string>)((owner, shared, topK, rerank) =>
                EntityQueries.SearchByVector(owner, shared, topK, rerank)),
            "entity_embedding_idx",
        };
        yield return new object[]
        {
            "Preference",
            (Func<bool, bool, int, bool, string>)((owner, shared, topK, rerank) =>
                PreferenceQueries.SearchByVector(owner, shared, topK, rerank)),
            "preference_embedding_idx",
        };
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

    [Fact]
    public void MessageSessionScope_PrefiltersBeforeExactCosine()
    {
        var scoped = MessageQueries.SearchByVector(hasSessionFilter: true, topK: 123);

        scoped.Should().Contain("MATCH (:Conversation {session_id: $sessionId})-[:HAS_MESSAGE]->(node:Message)");
        scoped.Should().Contain("vector.similarity.cosine(node.embedding, $embedding)");
        scoped.Should().NotContain("db.index.vector.queryNodes");
        var matchIndex = scoped.IndexOf("session_id: $sessionId", StringComparison.Ordinal);
        var cosineIndex = scoped.IndexOf("vector.similarity.cosine", StringComparison.Ordinal);
        var limitIndex = scoped.IndexOf("LIMIT $limit", StringComparison.Ordinal);
        matchIndex.Should().BeLessThan(cosineIndex, "session filtering must precede similarity work");
        cosineIndex.Should().BeLessThan(limitIndex, "the requested limit must apply after scoring");

        var unscoped = MessageQueries.SearchByVector(hasSessionFilter: false, topK: 5);
        unscoped.Should().Contain("db.index.vector.queryNodes('message_embedding_idx', 5");
        unscoped.Should().NotContain("vector.similarity.cosine");
        unscoped.Should().NotContain("LIMIT $limit", "the unfiltered query shape must remain unchanged");
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
        cypher.Should().Contain("$boostFactor * log(1 + COALESCE(node.access_count, 0))",
            $"{label} must damp the access boost (BUG-R7)");
        cypher.Should().Contain("$maxBoost", $"{label} must cap the access boost (BUG-R7)");
        cypher.Should().NotContain("$boostFactor * COALESCE(node.access_count, 0)",
            $"{label} must not regress to the linear, uncapped, undecayed access boost");
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
