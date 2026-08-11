namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for ReasoningTrace and ReasoningStep operations.
/// </summary>
internal static class ReasoningQueries
{
    // ── ReasoningTrace ──────────────────────────────────────────

    /// <summary>Create a new ReasoningTrace node. <c>owner_id</c> (R1; null = shared/global) is set once.</summary>
    public const string AddTrace = @"
            CREATE (t:ReasoningTrace {
                id:           $id,
                session_id:   $sessionId,
                owner_id:     $ownerId,
                task:         $task,
                outcome:      $outcome,
                success:      $success,
                metadata:     $metadata
            })
            SET t.started_at   = datetime($startedAt),
                t.completed_at = CASE WHEN $completedAt IS NOT NULL THEN datetime($completedAt) ELSE null END
            RETURN t";

    /// <summary>Set the task embedding vector on a ReasoningTrace node.</summary>
    public const string SetTraceTaskEmbedding = "MATCH (t:ReasoningTrace {id: $id}) SET t.task_embedding = $taskEmbedding";

    /// <summary>Update an existing ReasoningTrace node.</summary>
    public const string UpdateTrace = @"
            MATCH (t:ReasoningTrace {id: $id})
            SET
                t.task         = $task,
                t.outcome      = $outcome,
                t.success      = $success,
                t.started_at   = datetime($startedAt),
                t.completed_at = CASE WHEN $completedAt IS NOT NULL THEN datetime($completedAt) ELSE null END,
                t.metadata     = $metadata
            RETURN t";

    /// <summary>Get a ReasoningTrace by id.</summary>
    public const string GetTraceById = "MATCH (t:ReasoningTrace {id: $id}) RETURN t";

    /// <summary>
    /// List a session's ReasoningTraces newest-first, with an optional owner/shared filter (R1). Unlike a
    /// by-id handle, a <c>session_id</c> can be shared or guessable, so this multi-row list keyed by it
    /// must be scopeable — a scoped caller sees only its own (and optionally shared) traces, never another
    /// owner's.
    /// </summary>
    public static string ListTracesBySession(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (t.owner_id = $ownerId OR t.owner_id IS NULL)"
                            : " AND t.owner_id = $ownerId";
        return @"
            MATCH (t:ReasoningTrace {session_id: $sessionId})
            WHERE true" + owner + @"
            RETURN t
            ORDER BY t.started_at DESC
            LIMIT $limit";
    }

    /// <summary>
    /// Lists traces across ALL sessions, newest first, paged via SKIP/LIMIT. Callers fetch one extra row
    /// ($limit is size+1) to detect a next page. Optionally owner-scoped (R1) — a cross-session list is not
    /// keyed by a private handle, so when scoped it returns only the owner's own (and, if includeShared,
    /// shared/global) traces.
    /// </summary>
    public static string ListAllTraces(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (t.owner_id = $ownerId OR t.owner_id IS NULL)"
                            : " AND t.owner_id = $ownerId";
        // Tie-break on id so SKIP/LIMIT paging is stable when traces share a started_at — without it,
        // equal-timestamp rows can reorder between page fetches, causing duplicates or missed rows.
        return @"
            MATCH (t:ReasoningTrace)
            WHERE true" + owner + @"
            RETURN t
            ORDER BY t.started_at DESC, t.id DESC
            SKIP $offset
            LIMIT $limit";
    }

    /// <summary>
    /// Delete ReasoningTrace nodes for a session (and their child ReasoningStep nodes), confined to a
    /// single R1 owner bucket so a session-keyed destructive write can never cross owners (the twin of the
    /// #56 <see cref="PruneSessionTraces"/> hardening — ReasoningTrace carries <c>owner_id</c>, and
    /// <c>session_id</c> is guessable/shareable). When <paramref name="ownerIsShared"/> the shared/global
    /// bucket only (<c>owner_id IS NULL</c>); otherwise the owner's own traces (<c>owner_id = $ownerId</c>).
    /// Owner A's clear therefore never deletes owner B's or the shared bucket's traces, and a null-owner
    /// clear never touches an owner's private traces. (A method, not a const, so it is excluded from the
    /// Cypher snapshot inventory — like PruneSessionTraces.)
    /// </summary>
    public static string DeleteBySession(bool ownerIsShared)
    {
        var owner = ownerIsShared ? " AND t.owner_id IS NULL" : " AND t.owner_id = $ownerId";
        return @"
        MATCH (t:ReasoningTrace {session_id: $sessionId})
        WHERE true" + owner + @"
        OPTIONAL MATCH (t)-[:HAS_STEP]->(s:ReasoningStep)
        DETACH DELETE t, s";
    }

    /// <summary>
    /// Retention prune (H): keep the newest <c>$keep</c> traces for a session and delete the older ones with
    /// their child ReasoningStep nodes. Returns the number pruned. Ordering is by <c>started_at</c> DESC, so
    /// the just-added trace (newest) is always retained. ALWAYS confines to exactly one R1 bucket — the
    /// owner's own traces (<c>owner_id = $ownerId</c>), or, when <paramref name="ownerIsShared"/>, the
    /// shared/global bucket only (<c>owner_id IS NULL</c>) — so this destructive write can never cross owners.
    /// (A method, not a const, so it is excluded from the Cypher snapshot inventory.)
    /// </summary>
    public static string PruneSessionTraces(bool ownerIsShared)
    {
        // A retention prune is a DESTRUCTIVE write keyed by a guessable session_id, so its owner clause must
        // NEVER collapse to "all owners" (the #40 regression). It always confines to a single bucket.
        var owner = ownerIsShared ? " AND t.owner_id IS NULL" : " AND t.owner_id = $ownerId";
        return @"
            MATCH (t:ReasoningTrace {session_id: $sessionId})
            WHERE true" + owner + @"
            WITH t ORDER BY t.started_at DESC
            SKIP $keep
            OPTIONAL MATCH (t)-[:HAS_STEP]->(s:ReasoningStep)
            WITH collect(DISTINCT t) AS traces, collect(DISTINCT s) AS steps, count(DISTINCT t) AS pruned
            FOREACH (x IN traces | DETACH DELETE x)
            FOREACH (x IN steps  | DETACH DELETE x)
            RETURN pruned";
    }

    /// <summary>
    /// Vector similarity search over ReasoningTrace task embeddings, with an optional success filter
    /// and an optional owner/shared filter (R1). When scoped, over-fetches <paramref name="topK"/>
    /// candidates then LIMITs to <c>$limit</c> after filtering, which <b>reduces but does not remove</b>
    /// starvation by higher-scoring foreign rows — the vector index cannot pre-filter on a property.
    /// The earlier "never starved" wording was measured and is false (a mean of 7 of 60 candidates
    /// reached the querying owner on a 50-owner corpus). Unlike the fact path, trace search does
    /// <b>not</b> escalate on an empty scoped result.
    /// </summary>
    public static string SearchByTaskVector(bool hasSuccessFilter, bool hasOwnerFilter, bool includeShared, int topK)
    {
        var conditions = new List<string> { "score >= $minScore" };
        if (hasSuccessFilter) conditions.Add("node.success = $successFilter");
        if (hasOwnerFilter)
            conditions.Add(includeShared
                ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)"
                : "node.owner_id = $ownerId");

        var whereClause = "WHERE " + string.Join(" AND ", conditions);

        return $@"
            CALL db.index.vector.queryNodes('task_embedding_idx', {topK}, $embedding)
            YIELD node, score
            {whereClause}
            RETURN node, score
            ORDER BY score DESC
            LIMIT $limit";
    }

    /// <summary>
    /// Point-in-time variant of <see cref="SearchByTaskVector"/>: vector similarity over ReasoningTrace
    /// task embeddings restricted to traces that had started at or before <c>$asOf</c>, with the same
    /// optional success and owner/shared filters and the same over-fetch-then-LIMIT anti-starvation.
    /// (A method, not a const, so it is excluded from the Cypher snapshot inventory.)
    /// </summary>
    public static string SearchByTaskVectorAsOf(bool hasSuccessFilter, bool hasOwnerFilter, bool includeShared, int topK)
    {
        var conditions = new List<string>
        {
            "score >= $minScore",
            "node.started_at <= datetime($asOf)"
        };
        if (hasSuccessFilter) conditions.Add("node.success = $successFilter");
        if (hasOwnerFilter)
            conditions.Add(includeShared
                ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)"
                : "node.owner_id = $ownerId");

        var whereClause = "WHERE " + string.Join(" AND ", conditions);

        return $@"
            CALL db.index.vector.queryNodes('task_embedding_idx', {topK}, $embedding)
            YIELD node, score
            {whereClause}
            RETURN node, score
            ORDER BY score DESC
            LIMIT $limit";
    }

    /// <summary>Create an INITIATED_BY relationship between a ReasoningTrace and a Message.</summary>
    public const string CreateInitiatedByRelationship = @"
                MATCH (t:ReasoningTrace {id: $traceId}), (m:Message {id: $messageId})
                MERGE (t)-[:INITIATED_BY]->(m)";

    /// <summary>Create HAS_TRACE and IN_SESSION relationships between a Conversation and a ReasoningTrace.</summary>
    public const string CreateConversationTraceRelationships = @"
                MATCH (c:Conversation {id: $conversationId}), (t:ReasoningTrace {id: $traceId})
                MERGE (c)-[:HAS_TRACE]->(t)
                MERGE (t)-[:IN_SESSION]->(c)";

    // ── ReasoningStep ───────────────────────────────────────────

    /// <summary>Create a new ReasoningStep and link it to its parent ReasoningTrace.</summary>
    public const string AddStep = @"
            MATCH (t:ReasoningTrace {id: $traceId})
            CREATE (s:ReasoningStep {
                id:          $id,
                trace_id:    $traceId,
                step_number: $stepNumber,
                thought:     $thought,
                action:      $action,
                observation: $observation,
                metadata:    $metadata,
                timestamp:   datetime()
            })
            CREATE (t)-[:HAS_STEP {order: $stepNumber}]->(s)
            RETURN s";

    /// <summary>Set the embedding vector on a ReasoningStep node.</summary>
    public const string SetStepEmbedding = "MATCH (s:ReasoningStep {id: $id}) SET s.embedding = $embedding";

    /// <summary>Get all ReasoningSteps for a trace, ordered by step number.</summary>
    public const string GetStepsByTrace = @"
            MATCH (t:ReasoningTrace {id: $traceId})-[:HAS_STEP]->(s:ReasoningStep)
            RETURN s
            ORDER BY s.step_number";

    /// <summary>Get a ReasoningStep by id.</summary>
    public const string GetStepById = "MATCH (s:ReasoningStep {id: $id}) RETURN s";

    // ── TOUCHED audit edges (reasoning step → entity) ───────────
    // Records which entities a reasoning step read or acted upon, for auditability/provenance.
    // Mirrors the Python reference's by-id variant: we link only to entities that already exist
    // (created via the resolution/extraction pipeline), never MERGE-create them here. The MERGE on
    // the edge is idempotent; recorded_at is stamped once on create. See SchemaConstants.RelationshipTypes.Touched.

    /// <summary>
    /// Link a ReasoningStep to one or more existing entities (by id) with a TOUCHED edge. Entities or
    /// steps that do not exist are silently skipped. Returns the number of entities linked.
    /// </summary>
    public const string RecordTouchedEntitiesByIds = @"
            MATCH (s:ReasoningStep {id: $stepId})
            UNWIND $entityIds AS entityId
            MATCH (e:Entity {id: entityId})
            MERGE (s)-[r:TOUCHED]->(e)
            ON CREATE SET r.recorded_at = datetime()
            RETURN count(r) AS linked";

    /// <summary>Get the ids of all entities a ReasoningStep touched, ordered by id.</summary>
    public const string GetTouchedEntityIds = @"
            MATCH (s:ReasoningStep {id: $stepId})-[:TOUCHED]->(e:Entity)
            RETURN e.id AS id
            ORDER BY e.id";
    /// <summary>
    /// Last-resort owner-scoped trace similarity search that does NOT use the global vector index.
    /// </summary>
    /// <remarks>
    /// Mirrors the fact and entity fallbacks. Reached only when the indexed path and its escalation
    /// have both returned nothing, and bounded by ONE owner's traces rather than by the corpus. The
    /// success filter is preserved: a filtered search that legitimately matches nothing must still
    /// return nothing here rather than being rescued into the wrong answer.
    /// </remarks>
    public static string SearchByTaskVectorOwnerScopedFallback(bool hasSuccessFilter, bool includeShared)
    {
        var owner = includeShared
            ? "(t.owner_id = $ownerId OR t.owner_id IS NULL)"
            : "t.owner_id = $ownerId";
        var success = hasSuccessFilter
            ? Environment.NewLine + "              AND t.success = $successFilter"
            : string.Empty;
        return $@"
            MATCH (t:ReasoningTrace)
            WHERE {owner}
              AND t.task_embedding IS NOT NULL{success}
            WITH t, vector.similarity.cosine(t.task_embedding, $embedding) AS score
            WHERE score >= $minScore
            RETURN t AS node, score
            ORDER BY score DESC
            LIMIT $limit";
    }

}
