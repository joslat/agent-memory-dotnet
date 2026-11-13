namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// ILC-33: identity-aware recall for people. Only confirmed identities are followed: an open proposal (a decision that
/// still has POSSIBLE_SAME_AS candidates) is listed unconfirmed, so its name makes a namesake ambiguous, and its facts are
/// never read. No name similarity authorises a join.
/// </summary>
internal static class PersonDetailQueries
{
    /// <summary>The PERSON identities in scope, the names they are known by and whether they are confirmed, bounded.</summary>
    public const string Identities = """
        MATCH (p:Entity)
        WHERE (NOT $hasOwner OR p.owner_id=$ownerId OR ($includeShared AND p.owner_id IS NULL))
          AND toUpper(p.type)='PERSON' AND p.invalidated_at IS NULL AND p.merged_into IS NULL
        MATCH (p)<-[:RESOLVES_TO]-(b:IdentityFactBinding)-[:DECIDED_BY]->(d:IdentityDecision)
        WHERE b.identity_decision_id=d.id AND b.owner_scope=d.owner_scope
          AND (NOT $hasOwner OR b.owner_id=$ownerId OR ($includeShared AND b.owner_id IS NULL))
        WITH p, b, NOT EXISTS { (d)-[:POSSIBLE_SAME_AS]->() } AS decided
        WITH p, collect(DISTINCT b.identity_observed_surface)[..20] AS observed, any(flag IN collect(decided) WHERE flag) AS confirmed
        ORDER BY p.id LIMIT $limit
        RETURN p.id AS id, [name IN [p.name] + coalesce(p.aliases, []) + observed WHERE name IS NOT NULL][..32] AS surfaces,
               confirmed
        """;

    /// <summary>The live facts bound to the named people, newest first, taken in turn from each person.</summary>
    public const string Read = """
        UNWIND range(0,size($personIds)-1) AS personRank
        WITH personRank,$personIds[personRank] AS personId
        MATCH (p:Entity {id:personId})
        WHERE toUpper(p.type)='PERSON' AND p.invalidated_at IS NULL AND p.merged_into IS NULL
          AND (NOT $hasOwner OR p.owner_id=$ownerId OR ($includeShared AND p.owner_id IS NULL))
        MATCH (f:Fact)-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding)-[:DECIDED_BY]->(d:IdentityDecision),
              (b)-[:RESOLVES_TO]->(p)
        WHERE NOT f.id IN $excludeIds AND f.invalidated_at IS NULL
          AND (f.valid_until IS NULL OR f.valid_until >= datetime($now))
          AND (NOT $hasOwner OR f.owner_id=$ownerId OR ($includeShared AND f.owner_id IS NULL))
          AND b.identity_decision_id=d.id AND b.owner_scope=d.owner_scope
          AND b.owner_scope=CASE WHEN f.owner_id IS NULL THEN 'shared' ELSE 'owned:'+f.owner_id END
          AND ((f.owner_id IS NULL AND b.owner_id IS NULL AND d.owner_id IS NULL) OR
               (f.owner_id=b.owner_id AND f.owner_id=d.owner_id))
          AND NOT EXISTS { (d)-[:POSSIBLE_SAME_AS]->() }
        WITH personRank, f ORDER BY personRank, f.created_at DESC, f.id
        WITH personRank, collect(DISTINCT f)[..$limit] AS bound
        UNWIND range(0,size(bound)-1) AS turn
        WITH bound[turn] AS f, min(turn*3+personRank) AS rank
        ORDER BY rank, f.id LIMIT $limit
        RETURN f {.*,embedding:null} AS f
        """;
}
