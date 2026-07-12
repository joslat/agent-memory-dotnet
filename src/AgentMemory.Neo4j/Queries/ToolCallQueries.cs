namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for ToolCall operations.
/// </summary>
internal static class ToolCallQueries
{
    /// <summary>
    /// Aggregates tool-usage stats grouped by tool name over ToolCall nodes reached through
    /// ReasoningTrace→ReasoningStep→ToolCall, optionally owner-scoped at the trace tier (R1) and optionally
    /// filtered to one tool. Success/failure classification matches UpsertToolInstance (status is stored
    /// lowercase: success vs error/failure/timeout). Deliberately does not read the global :Tool node.
    /// </summary>
    public static string GetStats(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (t.owner_id = $ownerId OR t.owner_id IS NULL)"
                            : " AND t.owner_id = $ownerId";
        return @"
            MATCH (t:ReasoningTrace)-[:HAS_STEP]->(:ReasoningStep)-[:USES_TOOL]->(tc:ToolCall)
            WHERE ($toolName IS NULL OR tc.tool_name = $toolName)" + owner + @"
            WITH tc.tool_name AS name, count(tc) AS total_calls,
                 sum(CASE WHEN tc.status = 'success' THEN 1 ELSE 0 END) AS successful_calls,
                 sum(CASE WHEN tc.status IN ['error', 'failure', 'timeout'] THEN 1 ELSE 0 END) AS failed_calls,
                 sum(coalesce(tc.duration_ms, 0)) AS total_duration_ms
            RETURN name, total_calls, successful_calls, failed_calls, total_duration_ms
            ORDER BY name";
    }

    /// <summary>Create a new ToolCall node and link it to its parent ReasoningStep.</summary>
    public const string Add = @"
            MATCH (s:ReasoningStep {id: $stepId})
            CREATE (tc:ToolCall {
                id:          $id,
                step_id:     $stepId,
                tool_name:   $toolName,
                arguments:   $arguments,
                result:      $result,
                status:      $status,
                duration_ms: $durationMs,
                error:       $error,
                metadata:    $metadata,
                timestamp:   datetime()
            })
            CREATE (s)-[:USES_TOOL]->(tc)
            RETURN tc";

    /// <summary>Create or update the Tool aggregate node and INSTANCE_OF relationship.</summary>
    public const string UpsertToolInstance = @"
                MATCH (tc:ToolCall {id: $id})
                MERGE (tool:Tool {name: $toolName})
                ON CREATE SET tool.created_at = datetime(),
                              tool.description = $description,
                              tool.total_calls = 0,
                              tool.successful_calls = 0,
                              tool.failed_calls = 0,
                              tool.total_duration_ms = 0
                MERGE (tc)-[:INSTANCE_OF]->(tool)
                SET tool.total_calls = COALESCE(tool.total_calls, 0) + 1,
                    tool.successful_calls = COALESCE(tool.successful_calls, 0) + CASE WHEN $status = 'success' THEN 1 ELSE 0 END,
                    tool.failed_calls = COALESCE(tool.failed_calls, 0) + CASE WHEN $status IN ['error', 'failure', 'timeout'] THEN 1 ELSE 0 END,
                    tool.total_duration_ms = COALESCE(tool.total_duration_ms, 0) + COALESCE($durationMs, 0),
                    tool.last_used_at = datetime()";

    /// <summary>Update an existing ToolCall node.</summary>
    public const string Update = @"
            MATCH (tc:ToolCall {id: $id})
            SET
                tc.tool_name   = $toolName,
                tc.arguments   = $arguments,
                tc.result      = $result,
                tc.status      = $status,
                tc.duration_ms = $durationMs,
                tc.error       = $error,
                tc.metadata    = $metadata
            RETURN tc";

    /// <summary>Get all ToolCalls for a step, ordered by tool name.</summary>
    public const string GetByStep = @"
            MATCH (s:ReasoningStep {id: $stepId})-[:USES_TOOL]->(tc:ToolCall)
            RETURN tc
            ORDER BY tc.tool_name";

    /// <summary>Get a ToolCall by id.</summary>
    public const string GetById = "MATCH (tc:ToolCall {id: $id}) RETURN tc";

    /// <summary>Create a TRIGGERED_BY relationship between a ToolCall and a Message.</summary>
    public const string CreateTriggeredByRelationship = @"
                MATCH (tc:ToolCall {id: $toolCallId}), (m:Message {id: $messageId})
                MERGE (tc)-[:TRIGGERED_BY]->(m)";
}
