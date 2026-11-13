namespace AgentMemory.Neo4j.Queries;

/// <summary>Explicit opt-in projections; ordinary relationship queries stay unchanged while off.</summary>
internal static class IdentityRelationshipReadQueries
{
    // Identifier fragments below are library constants, never caller-provided identifiers.
    private static string Owner(string variable, bool hasOwner, bool includeShared) => !hasOwner ? "" :
        includeShared ? $" AND ({variable}.owner_id = $ownerId OR {variable}.owner_id IS NULL)" : $" AND {variable}.owner_id = $ownerId";

    private const string EndpointOwners = """
        AND s.invalidated_at IS NULL AND s.merged_into IS NULL AND t.invalidated_at IS NULL AND t.merged_into IS NULL
        AND ((r.owner_id IS NULL AND s.owner_id IS NULL AND t.owner_id IS NULL) OR
            (r.owner_id IS NOT NULL AND (s.owner_id = r.owner_id OR s.owner_id IS NULL) AND
                (t.owner_id = r.owner_id OR t.owner_id IS NULL)))
        """;

    private static string Rows(string predicate, bool hasOwner, bool includeShared) => """
        CALL {
            MATCH (s:Entity)-[r:RELATED_TO]->(t:Entity)
            WHERE (
        """ + predicate + ") " + Owner("r",hasOwner,includeShared) + Owner("s",hasOwner,includeShared) + Owner("t",hasOwner,includeShared) + " " + EndpointOwners + " " + """
            RETURN r {.*} AS r, s, t
            UNION ALL
            MATCH (o:IdentityRelationshipObservation)-[:SOURCE_IDENTITY]->(sd:IdentityDecision)-[:RESOLVES_TO]->(s:Entity),
                  (o)-[:TARGET_IDENTITY]->(td:IdentityDecision)-[:RESOLVES_TO]->(t:Entity)
            WHERE o.owner_scope = sd.owner_scope AND o.owner_scope = td.owner_scope
                AND o.identity_source_decision_id = sd.id AND o.identity_target_decision_id = td.id
                AND o.owner_scope = CASE WHEN o.owner_id IS NULL THEN 'shared' ELSE 'owned:' + o.owner_id END
                AND ((o.owner_id IS NULL AND sd.owner_id IS NULL AND td.owner_id IS NULL) OR
                    (o.owner_id = sd.owner_id AND o.owner_id = td.owner_id))
                AND s.id <> t.id
            WITH o, s, t, {id:o.id, source_entity_id:s.id, target_entity_id:t.id, owner_id:o.owner_id,
                relation_type:o.relation_type, confidence:o.confidence, description:o.description, attributes:o.attributes,
                source_message_ids:o.identity_source_ids, created_at:o.created_at, valid_from:null,
                valid_until:o.valid_until, metadata:'{}'} AS r
            WHERE (
        """ + predicate + ") " + Owner("r",hasOwner,includeShared) + Owner("s",hasOwner,includeShared) + Owner("t",hasOwner,includeShared) + " " + EndpointOwners + " " + """
            RETURN r, s, t
        }
        """;

    internal static string GetById() => Rows("r.id = $id",false,true) + " RETURN r";
    internal static string GetByEntity(bool owner,bool shared) => Rows("s.id = $entityId OR t.id = $entityId",owner,shared) + " RETURN r";
    internal static string GetBySource(bool owner,bool shared) => Rows("s.id = $sourceEntityId",owner,shared) + " RETURN r";
    internal static string GetByTarget(bool owner,bool shared) => Rows("t.id = $targetEntityId",owner,shared) + " RETURN r";

    internal static string GetLiveAround(bool owner,bool shared) => Rows("""
        (s.id IN $entityIds OR t.id IN $entityIds OR (size($names) > 0 AND
            (toLower(s.name) IN $names OR toLower(t.name) IN $names OR
                any(a IN coalesce(s.aliases,[]) WHERE toLower(a) IN $names) OR
                any(a IN coalesce(t.aliases,[]) WHERE toLower(a) IN $names))))
        AND (r.valid_until IS NULL OR r.valid_until > datetime($now))
        """,owner,shared) + """
        WITH r, s, t,
            [i IN range(0,size($entityIds)-1) WHERE $entityIds[i] IN [s.id,t.id]] AS byId,
            [i IN range(0,size($names)-1) WHERE $names[i] IN [toLower(s.name),toLower(t.name)] +
                [a IN coalesce(s.aliases,[]) | toLower(a)] + [a IN coalesce(t.aliases,[]) | toLower(a)]] AS byName
        RETURN r, s.name AS sourceName, t.name AS targetName
        ORDER BY CASE WHEN size(byId) > 0 THEN byId[0] WHEN size(byName) > 0 THEN size($entityIds)+byName[0]
            ELSE size($entityIds)+size($names) END, r.confidence DESC, r.created_at DESC, r.id
        LIMIT $limit
        """;

    internal static string End(bool owner,bool shared) => """
        CALL {
            MATCH (o:IdentityRelationshipObservation {id:$id}) WHERE true
        """ + Owner("o",owner,shared) + """
            SET o.valid_until = coalesce(o.valid_until,datetime($endedAt))
            RETURN o.id AS id
            UNION ALL
            MATCH ()-[r:RELATED_TO {id:$id}]->() WHERE NOT EXISTS {
                MATCH (:IdentityRelationshipObservation {id:$id})
            }
        """ + Owner("r",owner,shared) + """
            SET r.valid_until = coalesce(r.valid_until,datetime($endedAt))
            RETURN r.id AS id
        }
        RETURN count(*) > 0 AS ended
        """;
}
