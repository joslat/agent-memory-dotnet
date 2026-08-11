using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Entity operations.
/// Each constant or method corresponds to exactly one repository method in
/// <see cref="AgentMemory.Neo4j.Repositories.Neo4jEntityRepository"/>.
/// </summary>
internal static class EntityQueries
{
    // ── UpsertAsync ────────────────────────────────────────────────────

    /// <summary>Merge an entity by id, setting all properties on create/match.</summary>
    public const string Upsert = @"
            MERGE (e:Entity {id: $id})
            ON CREATE SET
                e.owner_id           = $ownerId,
                e.name               = $name,
                e.canonical_name     = $canonicalName,
                e.type               = $type,
                e.subtype            = $subtype,
                e.description        = $description,
                e.confidence         = $confidence,
                e.aliases            = $aliases,
                e.attributes         = $attributes,
                e.source_message_ids = $sourceMessageIds,
                e.created_at         = datetime($createdAtUtc),
                e.metadata           = $metadata
            ON MATCH SET
                e.name               = $name,
                e.canonical_name     = $canonicalName,
                e.type               = $type,
                e.subtype            = $subtype,
                e.description        = $description,
                e.confidence         = $confidence,
                e.aliases            = $aliases,
                e.attributes         = $attributes,
                e.source_message_ids = $sourceMessageIds,
                e.metadata           = $metadata,
                e.updated_at         = datetime()
            RETURN e";

    // ── GetByIdAsync ───────────────────────────────────────────────────

    /// <summary>Get a single entity by id.</summary>
    public const string GetById = "MATCH (e:Entity {id: $id}) RETURN e";

    // ── ApplyConfidenceDeltaAsync (entity feedback) ────────────────────

    /// <summary>
    /// Nudges an entity's confidence by <c>$delta</c> (positive or negative), clamped to [0,1], and
    /// stamps <c>updated_at</c>. Backs the entity-feedback surface (reinforce/penalize). Honors an
    /// optional owner/shared filter (R1) so feedback cannot mutate another owner's private entity;
    /// null owner ⇒ unscoped. Returns the node (no row ⇒ not found or out of scope).
    /// </summary>
    public static string ApplyConfidenceDelta(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (e.owner_id = $ownerId OR e.owner_id IS NULL)"
                            : " AND e.owner_id = $ownerId";
        return $@"
            MATCH (e:Entity {{id: $id}})
            WHERE true{owner}
            SET e.confidence = CASE
                    WHEN e.confidence + $delta > 1.0 THEN 1.0
                    WHEN e.confidence + $delta < 0.0 THEN 0.0
                    ELSE e.confidence + $delta END,
                e.updated_at = datetime()
            RETURN e";
    }

    // ── GetByNameAsync ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the get-by-name query, optionally matching aliases, with an optional owner/shared
    /// filter (R1). Null owner ⇒ unscoped (today's behavior).
    /// </summary>
    public static string GetByName(bool includeAliases, bool hasOwnerFilter, bool includeShared)
    {
        var nameMatch = includeAliases
            ? "MATCH (e:Entity) WHERE (e.name = $name OR $name IN e.aliases)"
            : "MATCH (e:Entity) WHERE e.name = $name";
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (e.owner_id = $ownerId OR e.owner_id IS NULL)"
                            : " AND e.owner_id = $ownerId";
        return $"{nameMatch}{owner} RETURN e";
    }

    // ── SearchByVectorAsync ────────────────────────────────────────────

    /// <summary>
    /// Vector similarity search on entity embeddings, with an optional owner/shared filter (R1).
    /// Over-fetches <paramref name="topK"/> candidates then LIMITs to <c>$limit</c> after filtering. When
    /// <paramref name="recencyRerank"/> is set (D1) the clamped ACT-R retention score is blended into the
    /// order key; when unset the query is byte-for-byte today's semantic-only ranking.
    /// </summary>
    public static string SearchByVector(bool hasOwnerFilter, bool includeShared, int topK, bool recencyRerank = false) =>
        VectorRerank.Finish(
            new CypherBuilder()
                .WithVectorSearch("entity_embedding_idx", "$embedding", "node", topK)
                .Where("score >= $minScore")
                .And("node.invalidated_at IS NULL")
                .And(includeShared ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)" : "node.owner_id = $ownerId", when: hasOwnerFilter),
            recencyRerank);

    // ── GetByTypeAsync ─────────────────────────────────────────────────

    /// <summary>
    /// Get all entities of a given type, with an optional owner/shared filter (R1). This backs entity
    /// resolution: scoping the candidate set prevents one owner's incoming entity from resolving onto
    /// (and merging into) another owner's private entity — a cross-owner write-path leak. Null owner ⇒
    /// unscoped (single-tenant / admin behavior).
    /// </summary>
    public static string GetByType(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (e.owner_id = $ownerId OR e.owner_id IS NULL)"
                            : " AND e.owner_id = $ownerId";
        // Exclude soft-invalidated/superseded entities (R6-B): this is the entity-resolution candidate set,
        // so a re-extracted entity must not resolve onto (and merge into) a tombstoned node — which would
        // make the live re-assertion invisible. Mirrors the invalidated_at guard on SearchByVector.
        return $"MATCH (e:Entity {{type: $type}}) WHERE e.invalidated_at IS NULL{owner} RETURN e";
    }

    // ── SearchByNameAsync ──────────────────────────────────────────────

    /// <summary>
    /// Builds a case-insensitive name search query, optionally filtered by entity type and by an
    /// owner/shared scope (R1). When <paramref name="type"/> is non-null a WHERE condition on
    /// <c>e.type</c> is prepended. The name/canonical-name OR is parenthesized so the type and owner
    /// predicates AND correctly across it. Null owner ⇒ unscoped.
    /// </summary>
    public static string SearchByNameFiltered(string? type, bool hasOwnerFilter, bool includeShared) =>
        CypherBuilder.Match("(e:Entity)")
            .Where("e.type = $type", when: type is not null)
            .And("(toLower(e.name) CONTAINS toLower($name) OR toLower(e.canonical_name) CONTAINS toLower($name))")
            .And(includeShared ? "(e.owner_id = $ownerId OR e.owner_id IS NULL)" : "e.owner_id = $ownerId", when: hasOwnerFilter)
            .Return("e")
            .Build();

    // ── AddMentionAsync ────────────────────────────────────────────────

    /// <summary>Create MENTIONS relationship from Message to Entity.</summary>
    public const string AddMention = @"
            MATCH (m:Message {id: $messageId})
            MATCH (e:Entity {id: $entityId})
            MERGE (m)-[r:MENTIONS]->(e)
            ON CREATE SET r.confidence = $confidence, r.start_pos = $startPos, r.end_pos = $endPos, r.context = $context, r.created_at = datetime()";

    // ── AddMentionsBatchAsync ──────────────────────────────────────────

    /// <summary>Batch create MENTIONS relationships from a Message to multiple Entities.</summary>
    public const string AddMentionsBatch = @"
            MATCH (m:Message {id: $messageId})
            UNWIND $entityIds AS eid
            MATCH (e:Entity {id: eid})
            MERGE (m)-[r:MENTIONS]->(e)
            ON CREATE SET r.confidence = $confidence, r.created_at = datetime()";

    // ── AddSameAsRelationshipAsync ─────────────────────────────────────

    /// <summary>Merge SAME_AS relationship between two entities with confidence tracking.</summary>
    public const string AddSameAs = @"
            MATCH (e1:Entity {id: $entityId1})
            MATCH (e2:Entity {id: $entityId2})
            MERGE (e1)-[r:SAME_AS]->(e2)
            ON CREATE SET r.confidence = $confidence, r.match_type = $matchType, r.created_at = datetime(), r.status = $status
            ON MATCH SET r.confidence = CASE WHEN $confidence > r.confidence THEN $confidence ELSE r.confidence END, r.updated_at = datetime()";

    // ── GetSameAsEntitiesAsync ──────────────────────────────────────────

    /// <summary>Get all entities linked by SAME_AS to a given entity.</summary>
    public const string GetSameAsEntities = @"
            MATCH (e:Entity {id: $entityId})-[r:SAME_AS]-(other:Entity)
            RETURN other, r.confidence AS confidence, r.match_type AS matchType";

    // ── UpsertBatchAsync ───────────────────────────────────────────────

    /// <summary>Batch merge entities by id via UNWIND.</summary>
    public const string UpsertBatch = @"
            UNWIND $items AS item
            MERGE (e:Entity {id: item.id})
            ON CREATE SET
                e.owner_id           = item.owner_id,
                e.name               = item.name,
                e.canonical_name     = item.canonical_name,
                e.type               = item.type,
                e.subtype            = item.subtype,
                e.description        = item.description,
                e.confidence         = item.confidence,
                e.aliases            = item.aliases,
                e.attributes         = item.attributes,
                e.source_message_ids = item.source_message_ids,
                e.created_at         = datetime(item.created_at),
                e.metadata           = item.metadata
            ON MATCH SET
                e.name               = item.name,
                e.canonical_name     = item.canonical_name,
                e.type               = item.type,
                e.subtype            = item.subtype,
                e.description        = item.description,
                e.confidence         = item.confidence,
                e.aliases            = item.aliases,
                e.attributes         = item.attributes,
                e.source_message_ids = item.source_message_ids,
                e.metadata           = item.metadata,
                e.updated_at         = datetime()
            RETURN e";

    // ── CreateExtractedFromRelationshipAsync ────────────────────────────

    /// <summary>Merge EXTRACTED_FROM with optional confidence/position metadata.</summary>
    public const string CreateExtractedFrom = @"
                MATCH (e:Entity {id: $entityId}), (m:Message {id: $messageId})
                MERGE (e)-[r:EXTRACTED_FROM]->(m)
                ON CREATE SET r.confidence = $confidence, r.start_pos = $startPos, r.end_pos = $endPos, r.context = $context, r.created_at = datetime()
                ON MATCH SET r.confidence = CASE WHEN $confidence IS NOT NULL AND ($confidence > r.confidence OR r.confidence IS NULL) THEN $confidence ELSE r.confidence END";

    // ── MergeEntitiesAsync ─────────────────────────────────────────────

    /// <summary>
    /// Merge a source entity into a target entity, transferring relationships and aliases. Moves MENTIONS
    /// (message provenance), SAME_AS (dedup links), and all typed <c>RELATED_TO</c> relationships — both
    /// outgoing and incoming, with every property (incl. the stable relationship id) preserved — from the
    /// source onto the target. The merge is non-destructive: only edges that would collapse into a
    /// target→target self-loop are dropped; every real relationship is re-pointed, never discarded (duplicate
    /// same-typed edges are left for the consolidation layer, so temporally-distinct facts survive). When
    /// scoped (R1) BOTH source and target must be the owner's own (or shared), and only the owner's own (or
    /// shared) RELATED_TO edges are moved — so a merge can never reach across the isolation boundary into
    /// another owner's entity or relationship. Null scope ⇒ unscoped (admin). A self-merge
    /// (source id == target id) is guarded as a no-op by the repository.
    /// </summary>
    public static string MergeEntities(bool hasOwnerFilter, bool includeShared)
    {
        var guard = !hasOwnerFilter ? string.Empty
            : includeShared
                ? "WHERE (source.owner_id = $ownerId OR source.owner_id IS NULL) AND (target.owner_id = $ownerId OR target.owner_id IS NULL)\n            "
                : "WHERE source.owner_id = $ownerId AND target.owner_id = $ownerId\n            ";
        // Owner/shared filter for the RELATED_TO EDGE itself (edges carry their own owner_id). A scoped merge
        // must only move/delete IN-SCOPE edges, so another owner's relationship on a shared entity is left
        // untouched — consistent with RelationshipQueries applying the owner filter to scoped relationship reads.
        var edgeGuard = !hasOwnerFilter ? string.Empty
            : includeShared
                ? "WHERE (r.owner_id = $ownerId OR r.owner_id IS NULL)\n                "
                : "WHERE r.owner_id = $ownerId\n                ";
        return @"
            MATCH (source:Entity {id: $sourceEntityId})
            MATCH (target:Entity {id: $targetEntityId})
            " + guard + @"CALL (source, target) {
                MATCH (source)<-[:MENTIONS]-(m:Message)
                WHERE NOT (m)-[:MENTIONS]->(target)
                MERGE (m)-[:MENTIONS]->(target)
                RETURN count(*) AS mentionsTransferred
            }
            CALL (source, target) {
                MATCH (source)-[r:SAME_AS]-(other:Entity)
                WHERE other <> target AND NOT (target)-[:SAME_AS]-(other)
                MERGE (target)-[:SAME_AS {confidence: r.confidence, match_type: r.match_type, created_at: datetime()}]-(other)
                RETURN count(*) AS sameAsTransferred
            }
            // Re-point OUTGOING typed relationships: (source)-[RELATED_TO]->(x) ⇒ (target)-[RELATED_TO]->(x).
            // RELATED_TO is a single fixed edge type carrying the semantic relation as the relation_type
            // property, so every typed rel moves in pure Cypher (no APOC): recreate on target preserving every
            // property (incl. the stable id) via properties(r), then delete the source edge. The ONLY edges
            // dropped are those that would collapse into a self-loop (other end is target or source) — every
            // real relationship is re-pointed, never discarded, so no fact/provenance/temporal history is lost.
            // Duplicate same-typed edges are intentionally left for the dedup/consolidation layer: the merge is
            // deliberately NON-DESTRUCTIVE (matching the library's soft-invalidate model — it never hard-deletes
            // a real relationship, so temporally-distinct facts and their ids survive).
            CALL (source, target) {
                MATCH (source)-[r:RELATED_TO]->(x:Entity)
                " + edgeGuard + @"FOREACH (_ IN CASE WHEN x <> target AND x <> source THEN [1] ELSE [] END |
                    CREATE (target)-[nr:RELATED_TO]->(x)
                    SET nr = properties(r), nr.source_entity_id = target.id, nr.updated_at = datetime()
                )
                DELETE r
                RETURN count(r) AS relatedOutgoingMoved
            }
            // Re-point INCOMING typed relationships: (y)-[RELATED_TO]->(source) ⇒ (y)-[RELATED_TO]->(target).
            CALL (source, target) {
                MATCH (y:Entity)-[r:RELATED_TO]->(source)
                " + edgeGuard + @"FOREACH (_ IN CASE WHEN y <> target AND y <> source THEN [1] ELSE [] END |
                    CREATE (y)-[nr:RELATED_TO]->(target)
                    SET nr = properties(r), nr.target_entity_id = target.id, nr.updated_at = datetime()
                )
                DELETE r
                RETURN count(r) AS relatedIncomingMoved
            }
            SET source.merged_into = target.id, source.merged_at = datetime()
            WITH source, target,
                 coalesce(target.aliases, []) +
                 [x IN ([source.name] + coalesce(source.aliases, []))
                  WHERE NOT x IN coalesce(target.aliases, [])] AS mergedAliases
            SET target.aliases = mergedAliases,
                target.description = CASE
                    WHEN target.description IS NULL THEN source.description
                    WHEN source.description IS NULL OR target.description CONTAINS source.description THEN target.description
                    ELSE target.description + ' ' + source.description
                END,
                target.embedding = null,
                target.updated_at = datetime()
            RETURN source, target";
    }

    // ── RefreshEntitySearchFieldsAsync ──────────────────────────────────

    /// <summary>Refresh search-relevant fields (aliases cleanup, updated_at).</summary>
    public const string RefreshSearchFields = @"
            MATCH (e:Entity {id: $entityId})
            SET e.updated_at = datetime($updatedAt),
                e.aliases    = [x IN coalesce(e.aliases, []) WHERE x IS NOT NULL AND size(toString(x)) > 0]
            RETURN e";

    // ── SearchByLocationAsync ──────────────────────────────────────────

    /// <summary>
    /// Spatial proximity search within a radius (km converted to meters by caller), with an optional
    /// owner/shared filter (R1) so one user cannot enumerate another's locations by sweeping coordinates.
    /// </summary>
    public static string SearchByLocation(bool hasOwnerFilter, bool includeShared)
    {
        var owner = OwnerAndClause(hasOwnerFilter, includeShared);
        return @"
            MATCH (e:Entity)
            WHERE e.location IS NOT NULL
              AND point.distance(e.location, point({latitude: $lat, longitude: $lon})) < $radiusMeters" + owner + @"
            RETURN e
            ORDER BY point.distance(e.location, point({latitude: $lat, longitude: $lon}))
            LIMIT $limit";
    }

    // ── SearchInBoundingBoxAsync ────────────────────────────────────────

    /// <summary>Spatial bounding-box search, with an optional owner/shared filter (R1).</summary>
    public static string SearchInBoundingBox(bool hasOwnerFilter, bool includeShared)
    {
        var owner = OwnerAndClause(hasOwnerFilter, includeShared);
        return @"
            MATCH (e:Entity)
            WHERE e.location IS NOT NULL
              AND point.withinBBox(
                    e.location,
                    point({longitude: $minLon, latitude: $minLat}),
                    point({longitude: $maxLon, latitude: $maxLat}))" + owner + @"
            RETURN e
            LIMIT $limit";
    }

    /// <summary>The owner/shared AND-clause for entity alias <c>e</c> (R1), or empty when unscoped.</summary>
    private static string OwnerAndClause(bool hasOwnerFilter, bool includeShared) =>
        !hasOwnerFilter ? string.Empty
        : includeShared ? " AND (e.owner_id = $ownerId OR e.owner_id IS NULL)"
                        : " AND e.owner_id = $ownerId";

    // ── GetPageWithoutEmbeddingAsync ────────────────────────────────────

    /// <summary>Get entities that have no embedding yet (for background embedding jobs).</summary>
    public const string GetPageWithoutEmbedding =
        "MATCH (e:Entity) WHERE e.embedding IS NULL RETURN e LIMIT $limit";

    // ── UpdateEmbeddingAsync ───────────────────────────────────────────

    /// <summary>Update embedding for a single entity (same as SharedFragments.SetEntityEmbedding).</summary>
    public const string UpdateEmbedding =
        "MATCH (e:Entity {id: $id}) SET e.embedding = $embedding";

    // ── DeleteAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Detach-delete an entity by id and report whether it existed. When scoped (R1) the delete only
    /// affects the owner's <b>own</b> entities — never another owner's, and never shared/global ones
    /// (deleting shared data on one user's behalf would affect everyone). Null scope ⇒ unscoped (admin).
    /// </summary>
    public static string Delete(bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND e.owner_id = $ownerId" : string.Empty;
        return @"
            MATCH (e:Entity {id: $entityId})
            WHERE true" + owner + @"
            DETACH DELETE e
            RETURN count(e) > 0 AS deleted";
    }

    // ── InvalidateAsync (D5 — transaction clock) ───────────────────────

    /// <summary>
    /// Soft-invalidate an entity by id: stamp <c>invalidated_at</c> so it drops out of live recall but is
    /// kept (auditable, recoverable, visible to as-of recall before invalidation). Owner-scoped (R1);
    /// idempotent (<c>coalesce</c> preserves the first invalidation time).
    /// </summary>
    public static string Invalidate(bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND e.owner_id = $ownerId" : string.Empty;
        return @"
            MATCH (e:Entity {id: $id})
            WHERE true" + owner + @"
            SET e.invalidated_at = coalesce(e.invalidated_at, datetime($now))
            RETURN count(e) > 0 AS invalidated";
    }

    // ── FindSimilarByEmbeddingAsync ─────────────────────────────────

    /// <summary>
    /// Vector search for potential duplicate entities, excluding self, with an optional owner/shared
    /// filter (R1) so a "find duplicates of my entity" surface cannot surface another owner's private
    /// entities. Null owner ⇒ unscoped (admin/maintenance).
    /// </summary>
    public static string FindSimilarByEmbedding(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (node.owner_id = $ownerId OR node.owner_id IS NULL)"
                            : " AND node.owner_id = $ownerId";
        // Exclude soft-invalidated/superseded entities (R6-B) so a "find duplicates of my entity" surface
        // does not present tombstoned nodes as live duplicate candidates. Mirrors FactQueries.FindDuplicate.
        return $@"
            MATCH (source:Entity {{id: $entityId}}) WHERE source.embedding IS NOT NULL
            CALL db.index.vector.queryNodes('entity_embedding_idx', $topK, source.embedding)
            YIELD node, score
            WHERE node.id <> $entityId AND score >= $minSimilarity AND node.invalidated_at IS NULL{owner}
            RETURN node, score
            ORDER BY score DESC
            LIMIT $limit";
    }

    // ── GetPendingDuplicatesAsync ───────────────────────────────────

    /// <summary>Get pending SAME_AS pairs for manual review.</summary>
    public const string GetPendingDuplicates = @"
            MATCH (a:Entity)-[s:SAME_AS {status: 'pending'}]->(b:Entity)
            RETURN a, b, s.confidence AS similarity, s.status
            ORDER BY s.confidence DESC
            LIMIT $limit";

    // ── GetDeduplicationStatsAsync ──────────────────────────────────

    /// <summary>SAME_AS relationship counts by status for deduplication monitoring.</summary>
    public const string GetDeduplicationStats = @"
            OPTIONAL MATCH ()-[s:SAME_AS]->()
            WITH s.status AS status, COUNT(s) AS cnt
            RETURN
              SUM(CASE WHEN status = 'pending' THEN cnt ELSE 0 END) AS pending,
              SUM(CASE WHEN status = 'confirmed' THEN cnt ELSE 0 END) AS confirmed,
              SUM(CASE WHEN status = 'rejected' THEN cnt ELSE 0 END) AS rejected,
              SUM(CASE WHEN status = 'merged' THEN cnt ELSE 0 END) AS merged";

    // ── GetEntitiesFromMessageAsync ────────────────────────────────────

    /// <summary>Get all entities extracted from a specific message.</summary>
    public const string GetEntitiesFromMessage = @"
            MATCH (m:Message {id: $messageId})<-[:EXTRACTED_FROM]-(e:Entity)
            RETURN e ORDER BY e.name";
    /// <summary>
    /// Last-resort owner-scoped similarity search that does NOT use the global vector index.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>FactQueries.SearchByVectorOwnerScopedFallback</c>. The indexed path takes a GLOBAL
    /// top-K then filters to the owner, and widening rescues that only up to <c>MaxTopK</c> = 2,000 —
    /// measured on facts, the owner received 4 of 4 at 3,000 competing rows and <b>0 of 4 at
    /// 4,000</b>, against a production corpus of 36,489. This scores the owner's own rows directly:
    /// a scan, but bounded by ONE owner's data rather than by the corpus, and reached only when the
    /// indexed path and its escalation have both returned nothing.
    /// </remarks>
    public static string SearchByVectorOwnerScopedFallback(bool includeShared)
    {
        var owner = includeShared
            ? "(n.owner_id = $ownerId OR n.owner_id IS NULL)"
            : "n.owner_id = $ownerId";
        return $@"
            MATCH (n:Entity)
            WHERE {owner}
              AND n.embedding IS NOT NULL
            WITH n, vector.similarity.cosine(n.embedding, $embedding) AS score
            WHERE score >= $minScore
            RETURN n AS node, score
            ORDER BY score DESC
            LIMIT $limit";
    }

}
