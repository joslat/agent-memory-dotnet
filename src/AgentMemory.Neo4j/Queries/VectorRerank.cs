using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Shared tail for the long-term vector-recall queries (Fact/Entity/Preference <c>SearchByVector</c>):
/// the optional <b>D1 recency re-rank</b> blend over the over-fetched candidate pool, followed by
/// RETURN / ORDER BY / LIMIT. When <c>recencyRerank</c> is <c>false</c> the tail is byte-for-byte today's
/// query (<c>RETURN node, score · ORDER BY score DESC · LIMIT $limit</c>), so disabling the re-ranker is
/// zero behaviour change.
/// </summary>
/// <remarks>
/// Not a <c>*Queries</c> class and holds no <c>const string</c>, so it is excluded from
/// <see cref="CypherQueryRegistry"/> / the Cypher snapshot by design (these are method-built fragments).
/// The temporal term reuses the exact ACT-R decay curve from the prune path
/// (<c>confidence·e^(−λ·daysSince) + boost·access</c>) and is <b>clamped to [0,1]</b> before blending so
/// it is scale-comparable to the cosine <c>score</c>. It uses <c>COALESCE</c> (never <c>IS NULL</c>) on the
/// temporal fields, so the owner-only variant never emits an <c>owner_id IS NULL</c> clause.
/// </remarks>
internal static class VectorRerank
{
    // (confidence + damped, capped accessBoost)·e^(−λ·daysSince), with COALESCE fallbacks matching the
    // prune. BUG-R7: the boost was linear, uncapped, and outside the decay, so at the shipped 0.2
    // factor five accesses drove it to 1.0 — where the clamp below pinned retention at its maximum
    // permanently and every frequently-recalled item tied at the ceiling, destroying the confidence
    // and recency signal this blend exists to carry.
    private const string RetentionExpr =
        "(COALESCE(node.confidence, 0.5) + " +
        "CASE WHEN $boostFactor * log(1 + COALESCE(node.access_count, 0)) > $maxBoost " +
        "THEN $maxBoost ELSE $boostFactor * log(1 + COALESCE(node.access_count, 0)) END) " +
        "* exp(-$lambda * daysSince)";

    /// <summary>
    /// Appends the (optional) recency-rerank blend, then RETURN / ORDER BY / LIMIT, and builds the query.
    /// </summary>
    /// <param name="builder">A builder already carrying the CALL+YIELD and any owner WHERE clauses.</param>
    /// <param name="recencyRerank">When true, blend the clamped retention score into the order key.</param>
    public static string Finish(CypherBuilder builder, bool recencyRerank, bool omitEmbedding = false)
    {
        // The embedding is projected away at the FINAL return only. The recency branch below still
        // reads node.last_accessed_at and node.created_at in its WITH clauses, so stripping earlier
        // would silently disable the re-ranker rather than shrink the payload.
        //
        // `node {.*, embedding: NULL}` keeps every other property and yields a MAP, not a Node --
        // which is why the read sites need dictionary mappers. That is the documented trap here, and
        // the reason this is projected rather than removed with an all-but-one helper that Cypher
        // does not have.
        var projection = omitEmbedding ? "node {.*, embedding: NULL} AS node" : "node";

        if (recencyRerank)
        {
            builder = builder
                // daysSince since last access (or creation), as a non-negative float (mirrors the prune).
                .With("node, score, (datetime($now).epochMillis - COALESCE(node.last_accessed_at, node.created_at).epochMillis) / 86400000.0 AS rawDays")
                .With("node, score, CASE WHEN rawDays < 0 THEN 0.0 ELSE rawDays END AS daysSince")
                // Retention score clamped to [0,1] so it is scale-comparable to the cosine score.
                .With($"node, score, CASE WHEN ({RetentionExpr}) > 1.0 THEN 1.0 ELSE ({RetentionExpr}) END AS sTmp")
                // Convex blend: (1−w)·semantic + w·recency.
                .Return($"{projection}, ((1.0 - $tmpWeight) * score + $tmpWeight * sTmp) AS score");
        }
        else
        {
            builder = builder.Return($"{projection}, score");
        }

        return builder
            .OrderBy("score DESC")
            .Limit("$limit")
            .Build();
    }
}
