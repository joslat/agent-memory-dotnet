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
                f.category           = $category,
                f.confidence         = $confidence,
                f.valid_from         = CASE WHEN $validFrom IS NOT NULL THEN datetime($validFrom) ELSE f.valid_from END,
                f.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE f.valid_until END,
                f.source_message_ids = $sourceMessageIds,
                f.updated_at         = datetime($updatedAtUtc),
                f.metadata           = $metadata,
                f.invalidated_at     = null
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

    // ── Dedup-on-create ────────────────────────────────────────────────

    /// <summary>
    /// Finds the most-similar existing fact with the same subject+predicate within the same owner
    /// (matched by <c>owner_key</c>) whose cosine score ≥ <c>$threshold</c> — used to reinforce instead
    /// of creating a near-duplicate node. Over-fetches <paramref name="topK"/> candidates, returns top 1.
    /// </summary>
    public static string FindDuplicate(int topK) => $@"
            CALL db.index.vector.queryNodes('fact_embedding_idx', {topK}, $embedding)
            YIELD node, score
            WHERE score >= $threshold
              AND node.invalidated_at IS NULL
              AND toLower(node.subject) = toLower($subject)
              AND toLower(node.predicate) = toLower($predicate)
              AND node.owner_key = $ownerKey
            RETURN node, score
            ORDER BY score DESC
            LIMIT 1";

    /// <summary>Reinforce an existing fact reached by dedup: bump its confidence.</summary>
    public const string MarkDeduplicated = "MATCH (f:Fact {id: $id}) SET f.confidence = $confidence RETURN f";

    // ── SearchByVectorAsync ────────────────────────────────────────────

    /// <summary>
    /// Vector similarity search on fact embeddings, with an optional owner/shared filter (R1).
    /// Over-fetches <paramref name="topK"/> candidates then LIMITs to <c>$limit</c> after filtering, so
    /// an owner filter is never starved by higher-scoring foreign rows. When
    /// <paramref name="recencyRerank"/> is set (D1) the clamped ACT-R retention score is blended into the
    /// order key (<c>$tmpWeight</c>); when unset the query is byte-for-byte today's semantic-only ranking.
    /// </summary>
    public static string SearchByVector(bool hasOwnerFilter, bool includeShared, int topK, bool recencyRerank = false) =>
        VectorRerank.Finish(
            new CypherBuilder()
                .WithVectorSearch("fact_embedding_idx", "$embedding", "node", topK)
                .Where("score >= $minScore")
                .And("node.invalidated_at IS NULL")
                .And(includeShared ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)" : "node.owner_id = $ownerId", when: hasOwnerFilter),
            recencyRerank);

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

    /// <summary>
    /// Detach-delete a fact by id and report whether it existed. When scoped (R1) the delete only affects
    /// the owner's own facts — never another owner's, and never shared/global ones. Null ⇒ unscoped.
    /// </summary>
    public static string Delete(bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND f.owner_id = $ownerId" : string.Empty;
        return @"
            MATCH (f:Fact {id: $factId})
            WHERE true" + owner + @"
            DETACH DELETE f
            RETURN count(f) > 0 AS deleted";
    }

    // ── InvalidateAsync (D5 — transaction clock) ───────────────────────

    /// <summary>
    /// Soft-invalidate a fact by id: stamp <c>invalidated_at</c> so it drops out of live recall but is
    /// kept — auditable, recoverable, and still visible to as-of recall for times before invalidation.
    /// Owner-scoped (R1): when set, only the owner's own fact is invalidated, never another owner's,
    /// never shared/global. Idempotent — <c>coalesce</c> preserves the first invalidation time.
    /// </summary>
    public static string Invalidate(bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND f.owner_id = $ownerId" : string.Empty;
        return @"
            MATCH (f:Fact {id: $id})
            WHERE true" + owner + @"
            SET f.invalidated_at = coalesce(f.invalidated_at, datetime($now))
            RETURN count(f) > 0 AS invalidated";
    }

    // ── SupersedeAsync (D7 — contradiction → supersession) ─────────────

    /// <summary>
    /// Supersede a loser fact with a winner: close the loser non-destructively (stamp both
    /// <c>invalidated_at</c> — transaction clock, drops it from live recall — and <c>valid_until</c> —
    /// valid-time clock, closes its real-world window) and link <c>(loser)-[:SUPERSEDED_BY]-&gt;(winner)</c>.
    /// Nothing is deleted: the loser stays visible to as-of recall for times before supersession.
    /// Owner-scoped (R1): when set, <b>both</b> facts must belong to the owner — a scoped supersede can
    /// neither read nor mutate another owner's facts. Idempotent — <c>coalesce</c> preserves the first
    /// timestamps and <c>MERGE</c> keeps the edge unique.
    /// </summary>
    public static string Supersede(bool hasOwnerFilter)
    {
        var loserOwner = hasOwnerFilter ? " AND loser.owner_id = $ownerId" : string.Empty;
        var winnerOwner = hasOwnerFilter ? " AND winner.owner_id = $ownerId" : string.Empty;
        // Same-owner guard (R1): loser and winner must belong to the same owner — even on the unscoped
        // (admin) path — so a cross-owner :SUPERSEDED_BY link can never be created. `loser <> winner`
        // rejects a self-supersede (same id), which would otherwise invalidate a live node and create a
        // :SUPERSEDED_BY self-loop while reporting success.
        return @"
            MATCH (loser:Fact {id: $loserId})
            WHERE true" + loserOwner + @"
            MATCH (winner:Fact {id: $winnerId})
            WHERE true" + winnerOwner + @"
              AND coalesce(loser.owner_id, '*') = coalesce(winner.owner_id, '*')
              AND loser <> winner
            SET loser.invalidated_at = coalesce(loser.invalidated_at, datetime($now)),
                loser.valid_until    = coalesce(loser.valid_until, datetime($now))
            MERGE (loser)-[:SUPERSEDED_BY]->(winner)
            RETURN count(loser) > 0 AS superseded";
    }

    // ── FindByTripleAsync ──────────────────────────────────────────────

    /// <summary>
    /// Case-insensitive lookup of a fact by its subject/predicate/object triple, with an optional
    /// owner/shared filter (R1) so a triple lookup cannot reach into another owner's private facts.
    /// Null owner ⇒ unscoped.
    /// </summary>
    public static string FindByTriple(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (f.owner_id = $ownerId OR f.owner_id IS NULL)"
                            : " AND f.owner_id = $ownerId";
        return $@"
            MATCH (f:Fact)
            WHERE toLower(f.subject) = toLower($subject)
              AND toLower(f.predicate) = toLower($predicate)
              AND toLower(f.object) = toLower($object){owner}
            RETURN f LIMIT 1";
    }
}
