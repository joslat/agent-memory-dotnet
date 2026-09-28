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
    /// <remarks>
    /// Two orders, on purpose. WHICH facts get a slot: the most mentioned, then the most recently touched,
    /// so a new job or city reaches the block instead of the oldest facts holding every slot forever.
    /// HOW they are written: by creation, so re-mentioning a fact that is already in the block does not
    /// reshuffle the text (a reshuffled text defeats the hash short-circuit and prompt-prefix caching).
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <c>$byMentions</c> slots go to the most mentioned facts, then <c>$recent</c> slots to the most
    /// recently LEARNED of the rest (by creation: a re-mention is a touch, and the point is new facts) (<c>WorkingMemoryOptions.RecentStableFactSlots</c>), so a fact said once
    /// still gets in when the mention slots are full. <c>[null]</c> keeps a row when there is no rest, and
    /// no <c>CALL</c> subquery is used, so this runs on every Neo4j 5.x.
    /// </para>
    /// </remarks>
    public const string SelectStableFacts = @"
            MATCH (f:Fact {owner_id: $ownerId})
            WHERE f.invalidated_at IS NULL
              AND (coalesce(f.valid_from, f.occurred_on) IS NULL OR coalesce(f.valid_from, f.occurred_on) <= datetime($now))
              AND (f.valid_until IS NULL OR f.valid_until >  datetime($now))
              AND coalesce(f.mention_count, 1) >= $minMentions
            WITH f
            ORDER BY coalesce(f.mention_count, 1) DESC, coalesce(f.updated_at, f.created_at) DESC, f.id ASC
            WITH collect(f) AS ranked
            WITH ranked[0..$byMentions] AS top, ranked[$byMentions..] AS rest
            UNWIND (CASE WHEN size(rest) = 0 THEN [null] ELSE rest END) AS r
            WITH top, r
            ORDER BY r.created_at DESC, r.id ASC
            WITH top, [x IN collect(r) WHERE x IS NOT NULL][0..$recent] AS recent
            UNWIND top + recent AS f
            RETURN f.id AS id, f.created_at AS createdAt,
                   f.subject AS subject, f.predicate AS predicate, f.object AS object,
                   f.valid_from AS validFrom, f.valid_from_precision AS validFromPrecision,
                   f.valid_until AS validUntil, f.valid_until_precision AS validUntilPrecision,
                   f.occurred_on AS occurredOn, f.occurred_on_precision AS occurredOnPrecision
            ORDER BY f.created_at ASC, f.id ASC";

    /// <summary>
    /// 37.5. What the person talked about most since <c>$since</c>: entities of the owner named by live facts, counted
    /// by the distinct facts extracted in the window from live user messages (a batch linking one fact to several
    /// messages counts it once; what the agent said never counts), never the person (a self word, or their stated
    /// name, found by the same keys the name lookup uses). Ordered by mentions, then name, so the line is byte-stable.
    /// </summary>
    public const string SelectRecentTopics = @"
            MATCH (f:Fact {owner_id: $ownerId})-[:EXTRACTED_FROM]->(m:Message)
            WHERE f.invalidated_at IS NULL AND m.role = 'user' AND m.invalidated_at IS NULL
              AND m.timestamp >= datetime($since)
            WITH DISTINCT f
            UNWIND [f.subject, f.object] AS topic
            WITH topic, count(DISTINCT f) AS mentions
            WHERE mentions >= $minMentions
              AND NOT toLower(topic) IN $selfWords
              AND EXISTS { MATCH (e:Entity {owner_id: $ownerId}) WHERE toLower(e.name) = toLower(topic) AND e.invalidated_at IS NULL }
              AND NOT EXISTS {
                  MATCH (n:Fact {owner_id: $ownerId})
                  WHERE n.subject_key IN $selfKeys AND n.predicate_key IN $namingKeys
                    AND n.invalidated_at IS NULL AND (n.valid_until IS NULL OR n.valid_until > datetime($now))
                    AND toLower(n.object) = toLower(topic) }
            RETURN topic, mentions
            ORDER BY mentions DESC, topic ASC
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
    /// <remarks>
    /// <para>
    /// <b><c>merged_into</c> is checked as well as <c>invalidated_at</c>, and that is not a
    /// belt-and-braces filter.</b> A merge tombstones the source by stamping <c>merged_into</c> /
    /// <c>merged_at</c> — deliberately, so the fold stays auditable and recoverable — and it does
    /// NOT invalidate it. An <c>invalidated_at</c>-only filter therefore keeps naming an entity that
    /// was folded into another, which is the same "block asserts something no longer true" staleness
    /// the supersession canary exists to prevent, arriving by a different route. Found by the 30.4b
    /// merge-seam test: hooking the rebuild alone left the merged name in the block, so the seam
    /// would have been cosmetic without this.
    /// </para>
    /// </remarks>
    public const string SelectTopEntities = @"
            MATCH (e:Entity {owner_id: $ownerId})
            WHERE e.invalidated_at IS NULL AND e.merged_into IS NULL
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
                u.working_memory_valid_until = CASE WHEN $validUntil IS NULL THEN null ELSE datetime($validUntil) END,
                u.updated_at = datetime($now)";

    /// <summary>
    /// The next moment the block's content can change by itself: the earliest future
    /// <c>valid_from</c> or <c>valid_until</c> among the owner's live facts (null when none). Stored with
    /// the block; a read after it rebuilds, so a fact that expired is never served from a block built
    /// before it did.
    /// </summary>
    public const string NextValidityBoundary = @"
            MATCH (f:Fact {owner_id: $ownerId})
            WHERE f.invalidated_at IS NULL
              AND coalesce(f.mention_count, 1) >= $minMentions
            WITH [x IN [coalesce(f.valid_from, f.occurred_on), f.valid_until] WHERE x IS NOT NULL AND x > datetime($now)] AS upcoming
            UNWIND upcoming AS boundary
            RETURN min(boundary) AS boundary";

    /// <summary>
    /// Clears every owner's block (a prune across all owners), leaving each one due for a rebuild on
    /// its owner's next read. See <see cref="ClearBlock"/>.
    /// </summary>
    public const string ClearAllBlocks = @"
            MATCH (u:User)
            WHERE u.working_memory IS NOT NULL
            SET u.working_memory = null,
                u.working_memory_built_at = null,
                u.working_memory_hash = null,
                u.working_memory_valid_until = datetime($now),
                u.updated_at = datetime($now)";

    /// <summary>
    /// Reads the stored block, and its validity boundary even when there is no text: an empty or
    /// cleared block still says when it must be rebuilt.
    /// </summary>
    public const string GetBlock = @"
            MATCH (u:User {identifier: $ownerId})
            WHERE u.working_memory IS NOT NULL OR u.working_memory_valid_until IS NOT NULL
            RETURN u.working_memory AS block,
                   u.working_memory_built_at AS builtAt,
                   u.working_memory_hash AS hash,
                   u.working_memory_valid_until AS validUntil";

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
    /// <para>
    /// The text goes (a pruned or erased fact must not be served) but the block is marked due: its
    /// validity boundary is set to now, so the owner's next read rebuilds it from what remains rather
    /// than going without a profile until the owner happens to write again.
    /// </para>
    /// </remarks>
    public const string ClearBlock = @"
            MATCH (u:User {identifier: $ownerId})
            SET u.working_memory = null,
                u.working_memory_built_at = null,
                u.working_memory_hash = null,
                u.working_memory_valid_until = datetime($now),
                u.updated_at = datetime($now)";
}
