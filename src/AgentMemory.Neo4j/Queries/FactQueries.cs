using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Fact operations.
/// Each constant corresponds to exactly one repository method in
/// <see cref="AgentMemory.Neo4j.Repositories.Neo4jFactRepository"/>.
/// </summary>
public static class FactQueries
{
    // ── UpsertAsync ────────────────────────────────────────────────────

    /// <summary>Merge a fact by subject/predicate/object triple, setting all properties.</summary>
    public const string Upsert = @"
            MERGE (f:Fact {subject: $subject, predicate: $predicate, object: $object, owner_key: $ownerKey})
            ON CREATE SET
                f.id                 = $id,
                f.owner_id           = $ownerId,
                f.category           = $category,
                f.confidence         = $confidence,
                f.valid_from         = CASE WHEN $validFrom IS NOT NULL THEN datetime($validFrom) ELSE null END,
                f.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE null END,
                f.source_message_ids = $sourceMessageIds,
                f.created_at         = datetime($createdAtUtc),
                f.metadata           = $metadata
            ON MATCH SET
                f.id                 = $id,
                f.category           = $category,
                f.confidence         = $confidence,
                f.valid_from         = CASE WHEN $validFrom IS NOT NULL THEN datetime($validFrom) ELSE null END,
                f.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE null END,
                f.source_message_ids = $sourceMessageIds,
                f.updated_at         = datetime($updatedAtUtc),
                f.metadata           = $metadata
            RETURN f";

    // ── UpsertBatchAsync ───────────────────────────────────────────────

    /// <summary>Batch merge facts by id via UNWIND.</summary>
    public const string UpsertBatch = @"
            UNWIND $items AS item
            MERGE (f:Fact {id: item.id})
            ON CREATE SET
                f.subject            = item.subject,
                f.predicate          = item.predicate,
                f.object             = item.object,
                f.owner_id           = item.owner_id,
                f.owner_key          = item.owner_key,
                f.category           = item.category,
                f.confidence         = item.confidence,
                f.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE null END,
                f.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE null END,
                f.source_message_ids = item.source_message_ids,
                f.created_at         = datetime(item.created_at),
                f.metadata           = item.metadata
            ON MATCH SET
                f.subject            = item.subject,
                f.predicate          = item.predicate,
                f.object             = item.object,
                f.category           = item.category,
                f.confidence         = item.confidence,
                f.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE null END,
                f.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE null END,
                f.source_message_ids = item.source_message_ids,
                f.metadata           = item.metadata
            RETURN f";

    // ── GetByIdAsync ───────────────────────────────────────────────────

    /// <summary>Get a single fact by id.</summary>
    public const string GetById = "MATCH (f:Fact {id: $id}) RETURN f";

    // ── GetBySubjectAsync ──────────────────────────────────────────────

    /// <summary>Get all facts for a given subject, with an optional owner/shared filter (R1).</summary>
    public static string GetBySubject(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (f.owner_id = $ownerId OR f.owner_id IS NULL)"
                            : " AND f.owner_id = $ownerId";
        return $"MATCH (f:Fact) WHERE f.subject = $subject{owner} RETURN f";
    }

    // ── SearchByVectorAsync ────────────────────────────────────────────

    /// <summary>
    /// Vector similarity search on fact embeddings, with an optional owner/shared filter (R1).
    /// Over-fetches <paramref name="topK"/> candidates then LIMITs to <c>$limit</c> after filtering, so
    /// an owner filter is never starved by higher-scoring foreign rows.
    /// </summary>
    public static string SearchByVector(bool hasOwnerFilter, bool includeShared, int topK) =>
        new CypherBuilder()
            .WithVectorSearch("fact_embedding_idx", "$embedding", "node", topK)
            .Where("score >= $minScore")
            .And(includeShared ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)" : "node.owner_id = $ownerId", when: hasOwnerFilter)
            .Return("node, score")
            .OrderBy("score DESC")
            .Limit("$limit")
            .Build();

    // ── CreateExtractedFromRelationshipAsync ────────────────────────────

    /// <summary>Link a Fact to a Message via EXTRACTED_FROM.</summary>
    public const string CreateExtractedFrom = @"
                MATCH (f:Fact {id: $factId}), (m:Message {id: $messageId})
                MERGE (f)-[:EXTRACTED_FROM]->(m)";

    // ── CreateAboutRelationshipAsync ───────────────────────────────────

    /// <summary>Link a Fact to an Entity via ABOUT.</summary>
    public const string CreateAbout = @"
                MATCH (f:Fact {id: $factId}), (e:Entity {id: $entityId})
                MERGE (f)-[:ABOUT]->(e)";

    // ── CreateConversationFactRelationshipAsync ─────────────────────────

    /// <summary>Link a Conversation to a Fact via HAS_FACT.</summary>
    public const string CreateConversationFact = @"
                MATCH (c:Conversation {id: $conversationId}), (f:Fact {id: $factId})
                MERGE (c)-[:HAS_FACT]->(f)";

    // ── GetPageWithoutEmbeddingAsync ────────────────────────────────────

    /// <summary>Get facts that have no embedding yet (for background embedding jobs).</summary>
    public const string GetPageWithoutEmbedding =
        "MATCH (f:Fact) WHERE f.embedding IS NULL RETURN f LIMIT $limit";

    // ── UpdateEmbeddingAsync ───────────────────────────────────────────

    /// <summary>Update embedding for a single fact.</summary>
    public const string UpdateEmbedding =
        "MATCH (f:Fact {id: $id}) SET f.embedding = $embedding";

    // ── DeleteAsync ────────────────────────────────────────────────────

    /// <summary>Detach-delete a fact by id and report whether it existed.</summary>
    public const string Delete = @"
            MATCH (f:Fact {id: $factId})
            DETACH DELETE f
            RETURN count(f) > 0 AS deleted";

    // ── FindByTripleAsync ──────────────────────────────────────────────

    /// <summary>Case-insensitive lookup of a fact by its subject/predicate/object triple.</summary>
    public const string FindByTriple = @"
            MATCH (f:Fact)
            WHERE toLower(f.subject) = toLower($subject)
              AND toLower(f.predicate) = toLower($predicate)
              AND toLower(f.object) = toLower($object)
            RETURN f LIMIT 1";
}
