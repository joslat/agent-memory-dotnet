namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for ReasoningTrace and ReasoningStep operations.
/// </summary>
public static class ReasoningQueries
{
    // ── ReasoningTrace ──────────────────────────────────────────

    /// <summary>Create a new ReasoningTrace node.</summary>
    public const string AddTrace = @"
            CREATE (t:ReasoningTrace {
                id:           $id,
                session_id:   $sessionId,
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
    /// Vector similarity search over ReasoningTrace task embeddings (without success filter).
    /// </summary>
    public static string SearchByTaskVector(bool hasSuccessFilter)
    {
        var whereClause = hasSuccessFilter
            ? "WHERE score >= $minScore AND node.success = $successFilter"
            : "WHERE score >= $minScore";

        return $@"
            CALL db.index.vector.queryNodes('task_embedding_idx', $limit, $embedding)
            YIELD node, score
            {whereClause}
            RETURN node, score
            ORDER BY score DESC";
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
