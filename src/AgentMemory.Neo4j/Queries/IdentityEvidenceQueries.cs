namespace AgentMemory.Neo4j.Queries;

/// <summary>Owned evidence schema. Limits apply to IDs before bounded context projection.</summary>
internal static class IdentityEvidenceQueries
{
    private const string Visible = "(($ownerId IS NULL AND e.owner_id IS NULL) OR ($ownerId IS NOT NULL AND (e.owner_id = $ownerId OR ($includeShared AND e.owner_id IS NULL))))";
    private const string Typed = "toUpper(e.type) IN $types AND e.invalidated_at IS NULL AND e.merged_into IS NULL "
        + "AND NOT EXISTS { MATCH (decision:IdentityDecision)-[:RESOLVES_TO]->(resolved:Entity) "
        + "WHERE decision.identity_observation_key=e.identity_observation_key AND "
        + "decision.owner_scope=CASE WHEN e.owner_id IS NULL THEN 'shared' ELSE 'owned:'+e.owner_id END AND resolved.id<>e.id } ";

    /// <summary>
    /// The entities a mention names exactly: by name, by calendar key, or through a key or alias assertion. RVW-REST-IDENTITY
    /// (the review's identity-4): one index-backed branch per way of naming, then the visibility and type checks on what
    /// they found. As a single WHERE (name OR key OR EXISTS) no index could serve it: the owner's entities came by the
    /// owner index, but with shared memory included every Entity in the store was scanned, on every mention. The
    /// 'namespace' assertion (surface = the entity's own name = the mention) is covered by the name branch.
    /// </summary>
    internal const string ExactIds = """
        CALL () {
            MATCH (e:Entity) WHERE e.name = $name RETURN e
            UNION
            MATCH (e:Entity) WHERE $calendarKey IS NOT NULL AND e.identity_calendar_key = $calendarKey RETURN e
            UNION
            MATCH (a:IdentityEvidenceAssertion) WHERE a.lookup_key IN $keyLookups
            MATCH (e:Entity)-[:HAS_IDENTITY_EVIDENCE]->(a)
            WHERE a.assertion_kind = 'key' AND a.owner_scope IN $evidenceScopes
            RETURN e
            UNION
            MATCH (a:IdentityEvidenceAssertion) WHERE a.namespace = $namespace AND a.surface = $name
            MATCH (e:Entity)-[:HAS_IDENTITY_EVIDENCE]->(a)
            WHERE a.assertion_kind = 'alias' AND a.owner_scope IN $evidenceScopes
            RETURN e
        }
        WITH e
        WHERE
        """ + Visible + " AND " + Typed + """
        RETURN DISTINCT e.id AS id ORDER BY id LIMIT $sentinelLimit
        """;

    /// <summary>
    /// ER-OWNER-CROWDING: the whole store's index gives a pool of <c>$poolLimit</c> hits that is filtered to this scope only
    /// afterwards, so on a store shared by many owners the other owners' entities can fill the pool and leave this owner's
    /// near-names out (tier 1's arms: fewer proposals with another arm's records in the store, none of them shared). Each
    /// route therefore also scores this scope's own entities directly, in the same query, when the pool came back full and
    /// fewer than <c>$limit</c> of its hits are visible: one owner's entities by <c>entity_owner_idx</c>, the shared ones by
    /// <c>owner_key = '*'</c> (G-14's two seeks), so the read is bounded by this scope's data, never by the store's.
    /// Returns <c>pool</c> (the index's hit count), <c>pooled</c> (its visible hits) and <c>scanned</c> (the scope's own;
    /// empty unless the pool was full and short).
    /// </summary>
    internal static string LexicalIds(bool owned, bool shared) => $$"""
        CALL db.index.fulltext.queryNodes('entity_name', $lucene, {limit: $poolLimit}) YIELD node, score
        {{Pooled}}
        CALL (hits, pooled) {
            UNWIND CASE WHEN size(pooled) < $limit AND size(hits) >= $poolLimit THEN [true] ELSE [] END AS crowded
            MATCH (e:Entity) WHERE {{OwnScope(owned, shared)}}
            WITH e, size([term IN $namePatterns WHERE e.name =~ term]) AS inName,
                 size([term IN $namePatterns WHERE coalesce(e.description, '') =~ term]) AS inDescription
            WHERE (inName > 0 OR inDescription > 0) AND {{Typed}}
            WITH e, 2 * inName + inDescription AS score ORDER BY score DESC, size(e.name), e.id LIMIT $limit
            RETURN collect({id: e.id, score: toFloat(score)}) AS scanned
        }
        RETURN size(hits) AS pool, pooled, scanned
        """;

    /// <summary>The vector twin of <see cref="LexicalIds"/>: the scope's own entities scored by cosine, as the index scores them.</summary>
    internal static string VectorIds(bool owned, bool shared) => $$"""
        CALL db.index.vector.queryNodes('entity_embedding_idx', $poolLimit, $embedding) YIELD node, score
        {{Pooled}}
        CALL (hits, pooled) {
            UNWIND CASE WHEN size(pooled) < $limit AND size(hits) >= $poolLimit THEN [true] ELSE [] END AS crowded
            MATCH (e:Entity) WHERE {{OwnScope(owned, shared)}}
              AND e.embedding IS NOT NULL AND size(e.embedding) = size($embedding) AND {{Typed}}
            WITH e, vector.similarity.cosine(e.embedding, $embedding) AS score ORDER BY score DESC, e.id LIMIT $limit
            RETURN collect({id: e.id, score: score}) AS scanned
        }
        RETURN size(hits) AS pool, pooled, scanned
        """;

    /// <summary>The index's hits as one list, and the visible ones of the right type among them, best first.</summary>
    private const string Pooled = $$"""
        WITH collect({node: node, score: score}) AS hits
        CALL (hits) {
            UNWIND hits AS hit
            WITH hit.node AS e, hit.score AS score
            WHERE {{Visible}} AND {{Typed}}
            WITH e, score ORDER BY score DESC, e.id LIMIT $limit
            RETURN collect({id: e.id, score: score}) AS pooled
        }
        """;

    /// <summary>
    /// The scope's own entities, as an indexed predicate: <see cref="Visible"/> without its parameter tests, which no index
    /// can serve. G-14: the shared half by <c>owner_key = '*'</c>, a seek, where <c>owner_id IS NULL</c> reads every entity.
    /// </summary>
    internal static string OwnScope(bool owned, bool shared) =>
        owned && shared ? "(e.owner_id = $ownerId OR (e.owner_key = '*' AND e.owner_id IS NULL))"
        : owned ? "e.owner_id = $ownerId"
        : "(e.owner_key = '*' AND e.owner_id IS NULL)";

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
        // ER-ADJUDICATOR-BOUND-SOURCES: a confirmed mention adds no source to its target, so the turns its bound facts were
        // said in are read here: newest binding first, at most four, the same bindings `known` reads. Ids only; the
        // adjudicator loads the text through CandidateSources, with its visibility rule.
        CALL {
            WITH e
            OPTIONAL MATCH (f:Fact)-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding)-[:RESOLVES_TO]->(e)
            WHERE f.invalidated_at IS NULL AND b.owner_scope IN $evidenceScopes
            WITH b ORDER BY b.created_at DESC, b.id
            WITH collect(coalesce(b.identity_source_ids, [])) AS lists
            WITH reduce(ids = [], list IN lists | ids + [id IN list WHERE id IS NOT NULL AND NOT id IN ids]) AS ids
            RETURN ids[..4] AS boundSources
        }
        RETURN known, texts, boundSources, e {.id, .name, .type, .owner_id, .canonical_name,
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
