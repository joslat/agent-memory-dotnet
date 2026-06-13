using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Preference operations.
/// </summary>
public static class PreferenceQueries
{
    /// <summary>Upsert a Preference node by id.</summary>
    public const string Upsert = @"
            MERGE (p:Preference {id: $id})
            ON CREATE SET
                p.owner_id           = $ownerId,
                p.category           = $category,
                p.preference         = $preferenceText,
                p.context            = $context,
                p.confidence         = $confidence,
                p.source_message_ids = $sourceMessageIds,
                p.created_at         = datetime($createdAtUtc),
                p.metadata           = $metadata
            ON MATCH SET
                p.category           = $category,
                p.preference         = $preferenceText,
                p.context            = $context,
                p.confidence         = $confidence,
                p.source_message_ids = $sourceMessageIds,
                p.metadata           = $metadata
            RETURN p";

    /// <summary>Set the embedding vector on a Preference node.</summary>
    public const string SetEmbedding = "MATCH (p:Preference {id: $id}) SET p.embedding = $embedding";

    /// <summary>Create EXTRACTED_FROM relationships between a Preference and its source Messages.</summary>
    public const string CreateExtractedFromMessages = @"
                    MATCH (p:Preference {id: $id})
                    UNWIND $sourceMessageIds AS msgId
                    MATCH (m:Message {id: msgId})
                    MERGE (p)-[:EXTRACTED_FROM]->(m)";

    /// <summary>Get a Preference by id.</summary>
    public const string GetById = "MATCH (p:Preference {id: $id}) RETURN p";

    /// <summary>Get all Preferences by category, with an optional owner/shared filter (R1).</summary>
    public static string GetByCategory(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (p.owner_id = $ownerId OR p.owner_id IS NULL)"
                            : " AND p.owner_id = $ownerId";
        return $"MATCH (p:Preference) WHERE p.category = $category{owner} RETURN p";
    }

    /// <summary>
    /// Vector similarity search over Preference embeddings, with an optional owner/shared filter (R1).
    /// Over-fetches <paramref name="topK"/> candidates then LIMITs to <c>$limit</c> after filtering. When
    /// <paramref name="recencyRerank"/> is set (D1) the clamped ACT-R retention score is blended into the
    /// order key; when unset the query is byte-for-byte today's semantic-only ranking.
    /// </summary>
    public static string SearchByVector(bool hasOwnerFilter, bool includeShared, int topK, bool recencyRerank = false) =>
        VectorRerank.Finish(
            new CypherBuilder()
                .WithVectorSearch("preference_embedding_idx", "$embedding", "node", topK)
                .Where("score >= $minScore")
                .And("node.invalidated_at IS NULL")
                .And(includeShared ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)" : "node.owner_id = $ownerId", when: hasOwnerFilter),
            recencyRerank);

    // ── Dedup-on-create ────────────────────────────────────────────────

    /// <summary>
    /// Finds the most-similar existing preference in the same category within the same owner whose
    /// cosine score ≥ <c>$threshold</c> — used to reinforce instead of creating a near-duplicate node.
    /// Over-fetches <paramref name="topK"/> candidates, returns top 1.
    /// </summary>
    public static string FindDuplicate(int topK, bool ownerIsShared)
    {
        var ownerClause = ownerIsShared ? "node.owner_id IS NULL" : "node.owner_id = $ownerId";
        return $@"
            CALL db.index.vector.queryNodes('preference_embedding_idx', {topK}, $embedding)
            YIELD node, score
            WHERE score >= $threshold
              AND node.category = $category
              AND {ownerClause}
            RETURN node, score
            ORDER BY score DESC
            LIMIT 1";
    }

    /// <summary>Reinforce an existing preference reached by dedup: bump its confidence.</summary>
    public const string MarkDeduplicated = "MATCH (p:Preference {id: $id}) SET p.confidence = $confidence RETURN p";

    /// <summary>
    /// Delete a Preference and all its relationships. When scoped (R1) the delete only affects the
    /// owner's own preferences — never another owner's, and never shared/global ones. Null ⇒ unscoped.
    /// </summary>
    public static string Delete(bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND p.owner_id = $ownerId" : string.Empty;
        return "MATCH (p:Preference {id: $id}) WHERE true" + owner + " DETACH DELETE p";
    }

    // ── InvalidateAsync (D5 — transaction clock) ───────────────────────

    /// <summary>
    /// Soft-invalidate a preference by id: stamp <c>invalidated_at</c> so it drops out of live recall but
    /// is kept (auditable, recoverable, visible to as-of recall before invalidation). Owner-scoped (R1);
    /// idempotent (<c>coalesce</c> preserves the first invalidation time).
    /// </summary>
    public static string Invalidate(bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND p.owner_id = $ownerId" : string.Empty;
        return @"
            MATCH (p:Preference {id: $id})
            WHERE true" + owner + @"
            SET p.invalidated_at = coalesce(p.invalidated_at, datetime($now))
            RETURN count(p) > 0 AS invalidated";
    }

    // ── SupersedeAsync (D7 — supersession) ─────────────────────────────

    /// <summary>
    /// Supersede a loser preference with a winner: soft-invalidate the loser (stamp <c>invalidated_at</c>
    /// — the only temporal clock preferences carry in this schema — so it drops from live recall but is
    /// kept and stays visible to as-of recall before supersession) and link
    /// <c>(loser)-[:SUPERSEDED_BY]-&gt;(winner)</c>. Mirrors upstream <c>supersede_preference</c>
    /// (non-destructive, both kept). Owner-scoped (R1): both preferences must belong to the owner.
    /// Idempotent (<c>coalesce</c> + <c>MERGE</c>).
    /// </summary>
    public static string Supersede(bool hasOwnerFilter)
    {
        var loserOwner = hasOwnerFilter ? " AND loser.owner_id = $ownerId" : string.Empty;
        var winnerOwner = hasOwnerFilter ? " AND winner.owner_id = $ownerId" : string.Empty;
        // Same-owner guard (R1): loser and winner must belong to the same owner — even on the unscoped
        // (admin) path — so a cross-owner :SUPERSEDED_BY link can never be created.
        return @"
            MATCH (loser:Preference {id: $loserId})
            WHERE true" + loserOwner + @"
            MATCH (winner:Preference {id: $winnerId})
            WHERE true" + winnerOwner + @"
              AND coalesce(loser.owner_id, '*') = coalesce(winner.owner_id, '*')
            SET loser.invalidated_at = coalesce(loser.invalidated_at, datetime($now))
            MERGE (loser)-[:SUPERSEDED_BY]->(winner)
            RETURN count(loser) > 0 AS superseded";
    }

    /// <summary>Create an EXTRACTED_FROM relationship between a Preference and a Message.</summary>
    public const string CreateExtractedFromRelationship = @"
                MATCH (p:Preference {id: $preferenceId}), (m:Message {id: $messageId})
                MERGE (p)-[:EXTRACTED_FROM]->(m)";

    /// <summary>Create an ABOUT relationship between a Preference and an Entity.</summary>
    public const string CreateAboutRelationship = @"
                MATCH (p:Preference {id: $preferenceId}), (e:Entity {id: $entityId})
                MERGE (p)-[:ABOUT]->(e)";

    /// <summary>Create a HAS_PREFERENCE relationship between a Conversation and a Preference.</summary>
    public const string CreateConversationPreferenceRelationship = @"
                MATCH (c:Conversation {id: $conversationId}), (p:Preference {id: $preferenceId})
                MERGE (c)-[:HAS_PREFERENCE]->(p)";

    /// <summary>Get a page of Preferences that have no embedding.</summary>
    public const string GetPageWithoutEmbedding = "MATCH (p:Preference) WHERE p.embedding IS NULL RETURN p LIMIT $limit";

    /// <summary>Update the embedding vector on a Preference node (same as SetEmbedding).</summary>
    public const string UpdateEmbedding = SetEmbedding;
}
