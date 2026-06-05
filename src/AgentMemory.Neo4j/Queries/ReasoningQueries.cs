namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for ReasoningTrace and ReasoningStep operations.
/// </summary>
public static class ReasoningQueries
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

    /// <summary>List ReasoningTraces for a session, ordered by most recent.</summary>
    public const string ListTracesBySession = @"
            MATCH (t:ReasoningTrace {session_id: $sessionId})
            RETURN t
            ORDER BY t.started_at DESC
            LIMIT $limit";

    /// <summary>
    /// Delete all ReasoningTrace nodes for a session, including their child ReasoningStep nodes.
    /// </summary>
    public const string DeleteBySession = @"
        MATCH (t:ReasoningTrace {session_id: $sessionId})
        OPTIONAL MATCH (t)-[:HAS_STEP]->(s:ReasoningStep)
        DETACH DELETE t, s";

    /// <summary>
    /// Vector similarity search over ReasoningTrace task embeddings, with an optional success filter
    /// and an optional owner/shared filter (R1). When scoped, over-fetches <paramref name="topK"/>
    /// candidates then LIMITs to <c>$limit</c> after filtering, so the owner filter is never starved
    /// by higher-scoring foreign rows (the vector index cannot pre-filter on a property).
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
}
