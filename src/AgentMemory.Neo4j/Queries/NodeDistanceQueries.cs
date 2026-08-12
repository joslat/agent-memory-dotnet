namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Graph distance from a query-matched centroid entity to a set of candidate facts (R6).
/// </summary>
/// <remarks>
/// <para>
/// Vector similarity asks "does this row look like the query?"; graph distance asks "is this row
/// <i>about</i> what the query is about?". They disagree usefully: a fact whose wording is unremarkable
/// but which hangs directly off the entity the question names is often the answer, and a fact that
/// merely paraphrases the query can be about someone else entirely.
/// </para>
/// <para>
/// <b>One bounded query for the whole candidate set, never one per candidate.</b> A per-candidate
/// traversal would multiply a recall's database round trips by the limit — and it runs on the blocking
/// path, so a reranker that costs more than the retrieval it reorders is not an improvement.
/// </para>
/// </remarks>
internal static class NodeDistanceQueries
{
    /// <summary>
    /// The entity whose name or aliases best match the query, by vector similarity.
    /// </summary>
    /// <remarks>
    /// The centroid is chosen by the <i>same</i> embedding the retrieval used, so "near the centroid"
    /// and "similar to the query" are commensurable rather than two unrelated notions of relevance.
    /// Owner-scoped: a centroid drawn from another tenant's graph would silently reorder this tenant's
    /// results around a node they cannot see.
    /// </remarks>
    internal static string CentroidEntity(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (node.owner_id = $ownerId OR node.owner_id IS NULL)"
                            : " AND node.owner_id = $ownerId";
        return $@"
            CALL db.index.vector.queryNodes('entity_embedding_idx', $topK, $embedding)
            YIELD node, score
            WHERE score >= $minScore{owner}
            RETURN node.id AS entityId, score AS score
            ORDER BY score DESC
            LIMIT 1";
    }

    /// <summary>
    /// Hop distance from the centroid entity to each candidate fact, for the whole set at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded three ways, because an unbounded traversal on a dense graph is how a reranker becomes
    /// the slowest thing in a recall: the candidate list is the starting set (not the corpus), the
    /// path length is capped at <c>$maxHops</c>, and only <c>RELATED_TO</c>/<c>ABOUT</c> edges are
    /// followed — the two that carry meaning between an entity and a fact. <c>EXTRACTED_FROM</c> is
    /// deliberately excluded: it links to source messages, so following it would make every fact from
    /// a shared conversation look adjacent regardless of subject.
    /// </para>
    /// <para>
    /// Candidates with no path within the cap are simply absent from the result, which the caller
    /// reads as "no boost" — the alternative, returning a sentinel distance, invites arithmetic on a
    /// number that means "unreachable".
    /// </para>
    /// </remarks>
    internal static string DistanceToCandidates => @"
            MATCH (centroid:Entity {id: $centroidId})
            MATCH (f:Fact) WHERE f.id IN $candidateIds
            MATCH path = shortestPath((centroid)-[:RELATED_TO|ABOUT*..4]-(f))
            WHERE length(path) <= $maxHops
            RETURN f.id AS candidateId, length(path) AS hops";
}
