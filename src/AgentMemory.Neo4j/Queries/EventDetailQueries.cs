namespace AgentMemory.Neo4j.Queries;

/// <summary>One bounded identity traversal. No name, date, person or location similarity authorises a join.</summary>
internal static class EventDetailQueries
{
    public const string Read = """
        UNWIND range(0,size($seedIds)-1) AS seedRank
        WITH seedRank,$seedIds[seedRank] AS seedId
        MATCH (seed:Fact {id:seedId})-[:HAS_IDENTITY_BINDING]->(sb:IdentityFactBinding)
              -[:DECIDED_BY]->(sd:IdentityDecision), (sb)-[:RESOLVES_TO]->(event:Entity)
        WHERE seed.invalidated_at IS NULL AND (seed.valid_until IS NULL OR seed.valid_until >= datetime($now))
          AND (seed.occurred_on IS NOT NULL OR seed.valid_from IS NOT NULL)
          AND (NOT $hasOwner OR seed.owner_id=$ownerId OR ($includeShared AND seed.owner_id IS NULL))
          AND sb.identity_decision_id=sd.id AND sb.owner_scope=sd.owner_scope
          AND sb.owner_scope=CASE WHEN seed.owner_id IS NULL THEN 'shared' ELSE 'owned:'+seed.owner_id END
          AND ((seed.owner_id IS NULL AND sb.owner_id IS NULL AND sd.owner_id IS NULL) OR
               (seed.owner_id=sb.owner_id AND seed.owner_id=sd.owner_id))
          AND toUpper(event.type)='EVENT' AND event.invalidated_at IS NULL AND event.merged_into IS NULL
          AND (NOT $hasOwner OR event.owner_id=$ownerId OR ($includeShared AND event.owner_id IS NULL))
        WITH event,min(seedRank) AS seedRank ORDER BY seedRank,event.id LIMIT 20
        MATCH (f:Fact)-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding)-[:DECIDED_BY]->(d:IdentityDecision),
              (b)-[:RESOLVES_TO]->(event)
        WHERE NOT f.id IN $excludeIds AND NOT f.id IN $seedIds AND f.invalidated_at IS NULL
          AND (f.valid_until IS NULL OR f.valid_until >= datetime($now))
          AND (NOT $hasOwner OR f.owner_id=$ownerId OR ($includeShared AND f.owner_id IS NULL))
          AND b.identity_decision_id=d.id AND b.owner_scope=d.owner_scope
          AND b.owner_scope=CASE WHEN f.owner_id IS NULL THEN 'shared' ELSE 'owned:'+f.owner_id END
          AND ((f.owner_id IS NULL AND b.owner_id IS NULL AND d.owner_id IS NULL) OR
               (f.owner_id=b.owner_id AND f.owner_id=d.owner_id))
        WITH f,min(seedRank) AS seedRank ORDER BY seedRank,f.created_at,f.id LIMIT $limit
        RETURN f {.*,embedding:null} AS f
        """;
}
