namespace AgentMemory.Neo4j.Queries;

/// <summary>Owned evidence schema. Limits apply to IDs before bounded context projection.</summary>
internal static class IdentityEvidenceQueries
{
    private const string Visible = "(($ownerId IS NULL AND e.owner_id IS NULL) OR ($ownerId IS NOT NULL AND (e.owner_id = $ownerId OR ($includeShared AND e.owner_id IS NULL))))";
    private const string Typed = "toUpper(e.type) IN $types AND e.invalidated_at IS NULL AND e.merged_into IS NULL "
        + "AND NOT EXISTS { MATCH (decision:IdentityDecision)-[:RESOLVES_TO]->(resolved:Entity) "
        + "WHERE decision.identity_observation_key=e.identity_observation_key AND "
        + "decision.owner_scope=CASE WHEN e.owner_id IS NULL THEN 'shared' ELSE 'owned:'+e.owner_id END AND resolved.id<>e.id } ";

    internal const string ExactIds = """
        MATCH (e:Entity)
        WHERE
        """ + Visible + " AND " + Typed + """
        AND (e.name = $name OR ($calendarKey IS NOT NULL AND e.identity_calendar_key = $calendarKey) OR EXISTS {
            MATCH (e)-[:HAS_IDENTITY_EVIDENCE]->(a:IdentityEvidenceAssertion)
            WHERE a.owner_scope IN $evidenceScopes AND
              ((a.assertion_kind = 'key' AND a.lookup_key IN $keyLookups) OR
               (a.assertion_kind = 'alias' AND a.namespace = $namespace AND a.surface = $name) OR
               (a.assertion_kind = 'namespace' AND a.namespace = $namespace AND a.surface = e.name AND e.name = $name))
        })
        RETURN DISTINCT e.id AS id ORDER BY id LIMIT $sentinelLimit
        """;

    internal const string LexicalIds = """
        CALL db.index.fulltext.queryNodes('entity_name', $lucene, {limit: $poolLimit}) YIELD node AS e, score
        WHERE
        """ + Visible + " AND " + Typed + """
        RETURN e.id AS id, score ORDER BY score DESC, id LIMIT $limit
        """;

    internal const string VectorIds = """
        CALL db.index.vector.queryNodes('entity_embedding_idx', $poolLimit, $embedding) YIELD node AS e, score
        WHERE
        """ + Visible + " AND " + Typed + """
        RETURN e.id AS id, score ORDER BY score DESC, id LIMIT $limit
        """;

    // ILC-13: same-type entities observed in the window's context messages (through EXTRACTED_FROM, the message index
    // first), each resolved through its own decision to its current identity. Review-only nomination; bounded.
    internal const string ContextRootedIds = """
        UNWIND $contextIds AS contextId
        MATCH (:Message {id: contextId})<-[:EXTRACTED_FROM]-(o:Entity)
        OPTIONAL MATCH (d:IdentityDecision)-[:RESOLVES_TO]->(target:Entity)
        WHERE o.identity_observation_key IS NOT NULL AND d.identity_observation_key = o.identity_observation_key
          AND d.owner_scope = CASE WHEN o.owner_id IS NULL THEN 'shared' ELSE 'owned:' + o.owner_id END AND target.id <> o.id
        WITH DISTINCT coalesce(target, o) AS e
        WHERE
        """ + Visible + " AND " + Typed + """
        RETURN e.id AS id, 1.0 AS score ORDER BY id LIMIT $limit
        """;

    internal const string Hydrate = """
        MATCH (e:Entity) WHERE e.id IN $ids AND
        """ + Visible + " AND " + Typed + """
        WITH e ORDER BY e.id LIMIT $limit
        CALL {
            WITH e
            OPTIONAL MATCH (e)-[:HAS_IDENTITY_EVIDENCE]->(a:IdentityEvidenceAssertion)
            WHERE a.owner_scope IN $evidenceScopes
            WITH a ORDER BY a.id LIMIT $evidenceLimit
            RETURN collect(a {.*}) AS evidence
        }
        // IDENTITY-NAMESAKE-WRITER-MERGE: the candidate's own live facts, bound to it or linked to it, newest first and
        // bounded, so what it is known as ("is the designer on Atlas") can be held against what a turn calls a mention.
        CALL {
            WITH e
            CALL {
                WITH e
                MATCH (f:Fact)-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding)-[:RESOLVES_TO]->(e)
                WHERE f.invalidated_at IS NULL AND b.owner_scope IN $evidenceScopes
                RETURN f
                UNION
                WITH e
                MATCH (f:Fact)-[:ABOUT]->(e)
                WHERE f.invalidated_at IS NULL AND
                    (($ownerId IS NULL AND f.owner_id IS NULL) OR ($ownerId IS NOT NULL AND (f.owner_id = $ownerId OR ($includeShared AND f.owner_id IS NULL))))
                RETURN f
            }
            WITH f ORDER BY f.created_at DESC, f.id LIMIT 16
            RETURN collect({subject: left(coalesce(f.subject, ''), 256), predicate: left(coalesce(f.predicate, ''), 256),
                object: left(coalesce(f.object, ''), 256)}) AS known
        }
        // IKB-TIER2-RERUN: the writer may store no fact saying what the candidate is, while the sentence it was stored from
        // does ("Valentina Ríos is the designer on Atlas"). Up to four of its own source messages, visible as CandidateSources
        // has them, in the same statement.
        CALL {
            WITH e
            UNWIND coalesce(e.source_message_ids, [])[..4] AS sourceId
            MATCH (m:Message {id: sourceId})
            MATCH (c:Conversation {id: m.conversation_id})
            WHERE m.invalidated_at IS NULL AND m.content IS NOT NULL AND (
                ($ownerId IS NULL AND c.user_id IS NULL) OR
                ($ownerId IS NOT NULL AND (c.user_id = $ownerId OR ($includeShared AND c.user_id IS NULL))))
            WITH m ORDER BY m.id
            RETURN collect(left(m.content, 1024)) AS texts
        }
        RETURN known, texts, e {.id, .name, .type, .owner_id, .canonical_name,
            confidence: coalesce(e.confidence, 1.0), .created_at,
            description: left(coalesce(e.description, ''), 4096),
            aliases: coalesce(e.aliases, [])[..32],
            source_message_ids: coalesce(e.source_message_ids, [])[..32],
            attributes: CASE WHEN size(coalesce(e.attributes, '')) <= 4096 THEN e.attributes ELSE null END
        } AS entity, evidence, coalesce(e.identity_evidence_version, 0) AS version
        """;

    // ILC-09: a candidate's own source messages, with the same visibility rule the decision write re-validates
    // (IdentityDecisionQueries.ValidateContextualSources). Limited before any text is returned.
    internal const string CandidateSources = """
        UNWIND $ids AS id
        MATCH (m:Message {id: id})
        MATCH (c:Conversation {id: m.conversation_id})
        WHERE m.invalidated_at IS NULL AND m.content IS NOT NULL AND (
            ($ownerId IS NULL AND c.user_id IS NULL) OR
            ($ownerId IS NOT NULL AND (c.user_id = $ownerId OR ($includeShared AND c.user_id IS NULL))))
        WITH m ORDER BY m.id LIMIT $limit
        RETURN m.id AS id, m.content AS content
        """;

    internal const string LockEntity = """
        MATCH (e:Entity {id: $entityId})
        WHERE ($ownerId IS NULL AND e.owner_id IS NULL) OR ($ownerId IS NOT NULL AND e.owner_id = $ownerId)
        SET e.identity_evidence_version = coalesce(e.identity_evidence_version, 0)
        WITH e
        OPTIONAL MATCH (e)-[:HAS_IDENTITY_EVIDENCE]->(a:IdentityEvidenceAssertion {assertion_kind: 'kind'})
        WITH e, a ORDER BY a.id LIMIT 3
        RETURN e {.id, .name, .type, .canonical_name} AS entity, collect(a {.*}) AS kinds
        """;

    internal const string Assert = """
        MATCH (e:Entity {id: $entityId})
        MERGE (a:IdentityEvidenceAssertion {id: $assertionId})
        ON CREATE SET a += $properties, a.created_at = datetime(), a.owner_id = $ownerId
        MERGE (e)-[:HAS_IDENTITY_EVIDENCE]->(a)
        ON CREATE SET e.identity_evidence_version = coalesce(e.identity_evidence_version, 0) + 1
        """;

    internal const string SetCalendarKey = """
        MATCH (e:Entity {id: $entityId}) SET e.identity_calendar_key = $calendarKey
        """;
}
