namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Bounded memory-kind writes used by the coalesced extraction path. Each query folds node mutation,
/// embedding and message provenance into one round trip so an outer transaction does not retain locks
/// across per-item follow-up queries.
/// </summary>
internal static class FusedPersistenceQueries
{
    public const string EntityUpsertBatch = @"
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
            SET e.embedding = CASE
                WHEN item.embedding IS NOT NULL AND size(item.embedding) > 0 THEN item.embedding
                ELSE e.embedding END
            FOREACH (_ IN CASE
                WHEN item.latitude IS NOT NULL AND item.longitude IS NOT NULL THEN [1]
                ELSE [] END |
                SET e.location = point({latitude: item.latitude, longitude: item.longitude}))
            SET e:$(item.labels)
            WITH e, item
            CALL (e, item) {
                UNWIND item.source_message_ids AS msgId
                MATCH (m:Message {id: msgId})
                MERGE (e)-[:EXTRACTED_FROM]->(m)
                RETURN count(*) AS linked
            }
            RETURN e";

    public const string FactUpsertBatch = @"
            UNWIND $items AS item
            MERGE (f:Fact {subject_key: item.subject_key, predicate_key: item.predicate_key, object_key: item.object_key, owner_key: item.owner_key})
            ON CREATE SET
                f.subject            = item.subject,
                f.predicate          = item.predicate,
                f.object             = item.object,
                f.id                 = item.id,
                f.owner_id           = item.owner_id,
                f.category           = item.category,
                f.confidence         = item.confidence,
                f.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE null END,
                f.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE null END,
                f.source_message_ids = item.source_message_ids,
                f.created_at         = datetime(item.created_at),
                f.metadata           = item.metadata
            ON MATCH SET
                f.category           = item.category,
                f.confidence         = item.confidence,
                f.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE f.valid_from END,
                f.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE f.valid_until END,
                f.source_message_ids = item.source_message_ids,
                f.updated_at         = datetime(item.updated_at),
                f.metadata           = item.metadata,
                f.invalidated_at     = null
            SET f.embedding = CASE
                WHEN item.embedding IS NOT NULL AND size(item.embedding) > 0 THEN item.embedding
                ELSE f.embedding END
            WITH f, item
            CALL (f, item) {
                UNWIND item.source_message_ids AS msgId
                MATCH (m:Message {id: msgId})
                MERGE (f)-[:EXTRACTED_FROM]->(m)
                RETURN count(*) AS linked
            }
            RETURN f";

    public const string PreferenceUpsertBatch = @"
            UNWIND $items AS item
            MERGE (p:Preference {id: item.id})
            ON CREATE SET
                p.owner_id           = item.owner_id,
                p.category           = item.category,
                p.preference         = item.preference,
                p.context            = item.context,
                p.confidence         = item.confidence,
                p.source_message_ids = item.source_message_ids,
                p.created_at         = datetime(item.created_at),
                p.metadata           = item.metadata
            ON MATCH SET
                p.category           = item.category,
                p.preference         = item.preference,
                p.context            = item.context,
                p.confidence         = item.confidence,
                p.source_message_ids = item.source_message_ids,
                p.metadata           = item.metadata
            SET p.embedding = CASE
                WHEN item.embedding IS NOT NULL AND size(item.embedding) > 0 THEN item.embedding
                ELSE p.embedding END
            WITH p, item
            CALL (p, item) {
                UNWIND item.source_message_ids AS msgId
                MATCH (m:Message {id: msgId})
                MERGE (p)-[:EXTRACTED_FROM]->(m)
                RETURN count(*) AS linked
            }
            RETURN p";
}
