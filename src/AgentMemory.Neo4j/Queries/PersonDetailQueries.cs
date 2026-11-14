namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// ILC-33: identity-aware recall for people. Only confirmed identities are followed: an open proposal (a decision that
/// still has POSSIBLE_SAME_AS candidates) is listed unconfirmed, so its name makes a namesake ambiguous, and its facts are
/// never read. No name similarity authorises a join.
/// </summary>
internal static class PersonDetailQueries
{
    /// <summary>
    /// Every live person in scope, the names they are known by and whether facts are bound to them through a decided
    /// identity (confirmed), bounded. A person with no bound facts is listed too, unconfirmed (PR260-F4): an entity-only
    /// open proposal or an entity written outside identity still makes its name ambiguous. The observation of a decision
    /// confirmed as another person is not a person of its own.
    /// </summary>
    public const string Identities = """
        CALL {
          // REV1-M4: each scope is its own index seek (owner_id, or owner_key '*' for shared); an OR over them is a scan
          // of every owner's entities on every recall.
          MATCH (p:Entity) WHERE $hasOwner AND p.owner_id=$ownerId RETURN p
          UNION
          MATCH (p:Entity) WHERE $hasOwner AND $includeShared AND p.owner_key='*' RETURN p
          UNION
          MATCH (p:Entity) WHERE NOT $hasOwner RETURN p
        }
        WITH p WHERE toUpper(p.type)='PERSON' AND p.invalidated_at IS NULL AND p.merged_into IS NULL
          AND NOT EXISTS {
            MATCH (o:IdentityDecision)-[:RESOLVES_TO]->(t:Entity)
            WHERE p.identity_observation_key IS NOT NULL AND o.identity_observation_key=p.identity_observation_key
              AND o.owner_scope=CASE WHEN p.owner_id IS NULL THEN 'shared' ELSE 'owned:'+p.owner_id END
              AND t.id<>p.id AND NOT EXISTS { (o)-[:POSSIBLE_SAME_AS]->() } }
        // REV2-2: read no further than the bound (the caller names nobody from a full page).
        WITH p LIMIT $limit
        OPTIONAL MATCH (p)<-[:RESOLVES_TO]-(b:IdentityFactBinding)-[:DECIDED_BY]->(d:IdentityDecision)
        WHERE b.identity_decision_id=d.id AND b.owner_scope=d.owner_scope
          AND (NOT $hasOwner OR b.owner_id=$ownerId OR ($includeShared AND b.owner_id IS NULL))
        WITH p, b, CASE WHEN d IS NULL THEN false ELSE NOT EXISTS { (d)-[:POSSIBLE_SAME_AS]->() } END AS decided
        WITH p, collect(DISTINCT b.identity_observed_surface) AS observed, any(flag IN collect(decided) WHERE flag) AS confirmed
        WITH p, confirmed, [spelling IN [p.name] + coalesce(p.aliases, []) + observed WHERE spelling IS NOT NULL] AS spellings
        // REV1-L12: spellings that differ only in case are one name (the first is kept, the primary name first). toString
        // because the planner cannot tell the collected names are strings.
        WITH p, confirmed, [i IN range(0, size(spellings) - 1)
            WHERE NOT toLower(toString(spellings[i])) IN [j IN range(0, i - 1) | toLower(toString(spellings[j]))] | spellings[i]] AS names
        RETURN p.id AS id, names[..32] AS surfaces, size(names) <= 32 AS complete, confirmed
        ORDER BY id
        """;

    /// <summary>
    /// REV2-2: the first, light read: every spelling a live person in scope is known by (its name, its aliases, and the
    /// names facts bound to a person used), distinct, at most <c>$limit</c>. No per-person grouping and no decision checks;
    /// recall reads <see cref="Identities"/> only when one of these is in the question, or when this page comes back full.
    /// </summary>
    public const string Names = """
        CALL {
          MATCH (p:Entity) WHERE $hasOwner AND p.owner_id=$ownerId RETURN p
          UNION
          MATCH (p:Entity) WHERE $hasOwner AND $includeShared AND p.owner_key='*' RETURN p
          UNION
          MATCH (p:Entity) WHERE NOT $hasOwner RETURN p
        }
        WITH p WHERE toUpper(p.type)='PERSON' AND p.invalidated_at IS NULL AND p.merged_into IS NULL
        // REV4-1: no further than the people bound: with that many people nobody is named anyway.
        WITH p LIMIT $people
        // REV3-5: a person's bound names are found from the person, through its own bindings, never by reading every
        // binding in the store.
        CALL {
          WITH p UNWIND [p.name] + coalesce(p.aliases, []) AS spelling RETURN spelling
          UNION
          WITH p MATCH (p)<-[:RESOLVES_TO]-(b:IdentityFactBinding)
          WHERE NOT $hasOwner OR b.owner_id=$ownerId OR ($includeShared AND b.owner_id IS NULL)
          RETURN b.identity_observed_surface AS spelling
        }
        WITH DISTINCT spelling WHERE spelling IS NOT NULL
        RETURN toString(spelling) AS spelling LIMIT $limit
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
