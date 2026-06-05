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
    /// Over-fetches <paramref name="topK"/> candidates then LIMITs to <c>$limit</c> after filtering.
    /// </summary>
    public static string SearchByVector(bool hasOwnerFilter, bool includeShared, int topK) =>
        new CypherBuilder()
            .WithVectorSearch("preference_embedding_idx", "$embedding", "node", topK)
            .Where("score >= $minScore")
            .And(includeShared ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)" : "node.owner_id = $ownerId", when: hasOwnerFilter)
            .Return("node, score")
            .OrderBy("score DESC")
            .Limit("$limit")
            .Build();

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

    /// <summary>Delete a Preference and all its relationships.</summary>
    public const string Delete = "MATCH (p:Preference {id: $id}) DETACH DELETE p";

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
