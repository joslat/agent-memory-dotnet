namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher for the working-memory tier: compiling a per-owner profile block and storing it on
/// upstream's <c>:User</c> node.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every ORDER BY ends in <c>id ASC</c>.</b> That is not tidiness — the block's text must be
/// byte-stable between input changes, or a rebuild that merely reshuffled equal-ranked rows would
/// change the hash, write, move <c>built_at</c>, and defeat prompt-prefix caching. Deterministic
/// ordering is what makes the hash short-circuit meaningful.
/// </para>
/// <para>
/// <b>Selection is supersession-resolved and validity-gated.</b> A block asserting a superseded value
/// would manufacture failures in knowledge-update — the weakest measured non-episodic type — so
/// <c>invalidated_at IS NULL</c> is not optional here, and neither is the valid-time window: a fact
/// that becomes true next month is not part of who the user is today.
/// </para>
/// </remarks>
internal static class WorkingMemoryQueries
{
    /// <summary>Stable facts: live, currently valid, salient, deterministically ordered.</summary>
    public const string SelectStableFacts = @"
            MATCH (f:Fact {owner_id: $ownerId})
            WHERE f.invalidated_at IS NULL
              AND (f.valid_from  IS NULL OR f.valid_from  <= datetime($now))
              AND (f.valid_until IS NULL OR f.valid_until >  datetime($now))
              AND coalesce(f.mention_count, 1) >= $minMentions
            RETURN f.subject AS subject, f.predicate AS predicate, f.object AS object
            ORDER BY coalesce(f.mention_count, 1) DESC, f.created_at ASC, f.id ASC
            LIMIT $limit";

    /// <summary>Active preferences: live and above the confidence floor.</summary>
    public const string SelectActivePreferences = @"
            MATCH (p:Preference {owner_id: $ownerId})
            WHERE p.invalidated_at IS NULL
              AND coalesce(p.confidence, 0.0) >= $minConfidence
            RETURN p.category AS category, p.preference AS preference
            ORDER BY p.category ASC, coalesce(p.confidence, 0.0) DESC, p.id ASC
            LIMIT $limit";

    /// <summary>Top entities by salience.</summary>
    /// <remarks>
    /// <c>access_count</c> is a self-confirming proxy — what we retrieve gets retrieved more — and
    /// that is accepted for a top-6 list rather than solved here. It is recorded as a known weakness
    /// rather than presented as a ranking.
    /// </remarks>
    public const string SelectTopEntities = @"
            MATCH (e:Entity {owner_id: $ownerId})
            WHERE e.invalidated_at IS NULL
            RETURN e.name AS name, e.type AS type
            ORDER BY coalesce(e.access_count, 0) DESC, e.name ASC, e.id ASC
            LIMIT $limit";

    /// <summary>
    /// Upserts the block onto upstream's <c>:User</c> node, keyed by upstream's own unique property.
    /// </summary>
    /// <remarks>
    /// MERGE on <c>identifier</c> because that is what upstream's <c>user_identifier</c> constraint
    /// keys on; <c>owner_id</c> is written alongside so .NET's own scoping convention reads naturally
    /// on the same node. Both always agree.
    /// </remarks>
    public const string UpsertBlock = @"
            MERGE (u:User {identifier: $ownerId})
            ON CREATE SET u.id = $id, u.created_at = datetime($now)
            SET u.owner_id = $ownerId,
                u.working_memory = $block,
                u.working_memory_built_at = datetime($now),
                u.working_memory_hash = $hash,
                u.updated_at = datetime($now)";

    /// <summary>Reads the stored block.</summary>
    public const string GetBlock = @"
            MATCH (u:User {identifier: $ownerId})
            WHERE u.working_memory IS NOT NULL
            RETURN u.working_memory AS block,
                   u.working_memory_built_at AS builtAt,
                   u.working_memory_hash AS hash";

    /// <summary>Reads only the stored hash, for the rebuild short-circuit.</summary>
    public const string GetBlockHash = @"
            MATCH (u:User {identifier: $ownerId})
            RETURN u.working_memory_hash AS hash";

    /// <summary>
    /// Clears the stored block without deleting the node.
    /// </summary>
    /// <remarks>
    /// Removing the properties rather than the node: upstream owns <c>:User</c>, and deleting an
    /// identity node because our derived block failed to rebuild would destroy something that is not
    /// ours to destroy.
    /// </remarks>
    public const string ClearBlock = @"
            MATCH (u:User {identifier: $ownerId})
            SET u.working_memory = null,
                u.working_memory_built_at = null,
                u.working_memory_hash = null,
                u.updated_at = datetime($now)";
}
