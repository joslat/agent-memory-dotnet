using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// Advanced memory tools: record tool calls, export graph, find duplicates, extract and persist.
/// </summary>
[McpServerToolType]
internal sealed class AdvancedMemoryTools
{
    [McpServerTool(Name = "memory_record_tool_call"), Description("Records a tool call for a reasoning trace step. Associates a tool invocation with an existing reasoning step.")]
    public static async Task<string> MemoryRecordToolCall(
        IReasoningMemoryService reasoningMemory,
        [Description("The reasoning step ID this tool call belongs to")] string stepId,
        [Description("Name of the tool that was called")] string toolName,
        [Description("JSON-serialized arguments passed to the tool")] string input,
        [Description("JSON-serialized result from the tool (optional)")] string? output = null,
        [Description("Status of the call: Pending, Success, Error, Failure, Timeout, or Cancelled (default: Success)")] string status = "Success",
        CancellationToken cancellationToken = default)
    {
        // Only an OMITTED status defaults to Success. An unrecognized value (a typo like "failed", or a
        // numeric string) must NOT be silently coerced to Success — that would invert the recorded outcome
        // of a tool call in durable provenance. Return an error payload (matching MaintenanceTools' style).
        ToolCallStatus toolStatus;
        if (string.IsNullOrWhiteSpace(status))
        {
            toolStatus = ToolCallStatus.Success;
        }
        else if (!Enum.TryParse(status, ignoreCase: true, out toolStatus) || !Enum.IsDefined(toolStatus))
        {
            return ToolJsonContext.Serialize(new
            {
                error = $"unknown status '{status}' (expected one of: Pending, Success, Error, Failure, Timeout, Cancelled)"
            });
        }

        var toolCall = await reasoningMemory.RecordToolCallAsync(
            stepId, toolName, input, output, toolStatus,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToolJsonContext.Serialize(new
        {
            toolCall.ToolCallId,
            toolCall.StepId,
            toolCall.ToolName,
            toolCall.ArgumentsJson,
            toolCall.ResultJson,
            status = toolCall.Status.ToString(),
            toolCall.DurationMs,
            toolCall.Error
        });
    }

    [McpServerTool(Name = "memory_export_graph"), Description("Exports the memory graph or a session-scoped subset as structured JSON. Returns nodes and their relationships. Requires EnableGraphQuery = true in server options.")]
    public static async Task<string> MemoryExportGraph(
        IGraphQueryService graphQueryService,
        IOptions<McpServerOptions> options,
        [Description("Session identifier to scope the export (optional, exports all if omitted)")] string? sessionId = null,
        [Description("Export format: 'json' (default) or 'cypher'")] string format = "json",
        [Description("Maximum number of nodes to export (default: 100)")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.EnableGraphQuery)
            throw new McpException("memory_export_graph requires EnableGraphQuery = true in McpServerOptions.");

        limit = Math.Clamp(limit, 1, 1000); // guard against negative (Neo4j error) / huge (resource-exhaustion) limits

        // The stored schema uses snake_case `session_id` for the session key and `id` for a node's
        // logical id (there is no `sessionId`/`entityId` property). A Cypher reference to a missing
        // property silently evaluates to null, so the previous camelCase names made a session-scoped
        // export return zero rows and every relationship endpoint id come back null. Note: only
        // session-bearing nodes (Message/Conversation/ReasoningTrace) carry `session_id`, so a
        // session-scoped export surfaces those node types, not Entity/Fact/Preference. Endpoint ids fall
        // back to elementId() so mixed node types without an `id` still return a stable identifier.
        var nodeQuery = sessionId is null
            ? "MATCH (n) RETURN labels(n) AS labels, properties(n) AS props LIMIT $limit"
            : "MATCH (n) WHERE n.session_id = $sessionId RETURN labels(n) AS labels, properties(n) AS props LIMIT $limit";

        var relQuery = sessionId is null
            ? "MATCH (a)-[r]->(b) RETURN coalesce(a.id, elementId(a)) AS fromId, type(r) AS relType, coalesce(b.id, elementId(b)) AS toId, properties(r) AS relProps LIMIT $limit"
            : "MATCH (a)-[r]->(b) WHERE a.session_id = $sessionId AND b.session_id = $sessionId RETURN coalesce(a.id, elementId(a)) AS fromId, type(r) AS relType, coalesce(b.id, elementId(b)) AS toId, properties(r) AS relProps LIMIT $limit";

        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = (long)limit,
            ["sessionId"] = (object?)sessionId
        };

        var nodes = await graphQueryService.QueryAsync(nodeQuery, parameters, cancellationToken).ConfigureAwait(false);
        var relationships = await graphQueryService.QueryAsync(relQuery, parameters, cancellationToken).ConfigureAwait(false);

        if (format.Equals("cypher", StringComparison.OrdinalIgnoreCase))
        {
            return ToolJsonContext.Serialize(new
            {
                format = "cypher",
                sessionId,
                nodeCount = nodes.Count,
                relationshipCount = relationships.Count,
                note = "Full Cypher CREATE export is not supported via this tool. Use format=json for structured data, or use graph_query directly with APOC export procedures if available."
            });
        }

        return ToolJsonContext.Serialize(new
        {
            format = "json",
            sessionId,
            nodeCount = nodes.Count,
            relationshipCount = relationships.Count,
            nodes,
            relationships
        });
    }

    [McpServerTool(Name = "memory_find_duplicates"), Description("Finds potential duplicate entities based on name containment similarity. Returns pairs of entities whose names are substrings of each other. Requires EnableGraphQuery = true.")]
    public static async Task<string> MemoryFindDuplicates(
        IGraphQueryService graphQueryService,
        IOptions<McpServerOptions> options,
        [Description("Minimum similarity threshold from 0.0 to 1.0 based on name length ratio (default: 0.8)")] double threshold = 0.8,
        [Description("Maximum number of duplicate pairs to return (default: 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.EnableGraphQuery)
            throw new McpException("memory_find_duplicates requires EnableGraphQuery = true in McpServerOptions.");

        limit = Math.Clamp(limit, 1, 1000); // guard against negative (Neo4j error) / huge (resource-exhaustion) limits

        // Entities store their logical id under `id` (not `entityId`). `elementId(a) < elementId(b)`
        // already guarantees the two nodes are distinct, so no separate inequality on the id is needed.
        const string query = """
            MATCH (a:Entity), (b:Entity)
            WHERE elementId(a) < elementId(b)
              AND a.invalidated_at IS NULL AND b.invalidated_at IS NULL
              AND NOT (a)-[:SAME_AS]-(b)
              AND (toLower(a.name) CONTAINS toLower(b.name)
                   OR toLower(b.name) CONTAINS toLower(a.name))
            WITH a, b,
                 CASE WHEN size(a.name) >= size(b.name)
                      THEN toFloat(size(b.name)) / size(a.name)
                      ELSE toFloat(size(a.name)) / size(b.name)
                 END AS similarity
            WHERE similarity >= $threshold
            RETURN a.id AS entityAId, a.name AS nameA, a.type AS typeA,
                   b.id AS entityBId, b.name AS nameB, b.type AS typeB,
                   similarity
            ORDER BY similarity DESC
            LIMIT $limit
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["threshold"] = threshold,
            ["limit"] = (long)limit
        };

        var results = await graphQueryService.QueryAsync(query, parameters, cancellationToken).ConfigureAwait(false);

        return ToolJsonContext.Serialize(new
        {
            pairCount = results.Count,
            threshold,
            pairs = results
        });
    }

    [McpServerTool(Name = "extract_and_persist"), Description("Triggers memory extraction on a message and persists the extracted entities, facts, preferences, and relationships to long-term memory. Returns a summary of what was extracted.")]
    public static async Task<string> ExtractAndPersist(
        IMemoryService memoryService,
        IIdGenerator idGenerator,
        IClock clock,
        IOptions<McpServerOptions> options,
        [Description("The message text to extract from")] string messageText,
        [Description("Session identifier (optional, uses default if omitted)")] string? sessionId = null,
        [Description("Conversation identifier (optional, defaults to session ID)")] string? conversationId = null,
        [Description("Role of the message sender (default: 'user')")] string role = "user",
        [Description("Owner/user identifier (optional, R1). When set, extracted memories are owner-stamped and resolution is owner-scoped; null = stored as shared/global (visible to all owners on recall).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var sid = sessionId ?? options.Value.DefaultSessionId;
        var cid = conversationId ?? sid;

        var message = new Message
        {
            MessageId = idGenerator.GenerateId(),
            SessionId = sid,
            ConversationId = cid,
            Role = role,
            Content = messageText,
            TimestampUtc = clock.UtcNow
        };

        var result = await memoryService.ExtractAndPersistAsync(
            new ExtractionRequest
            {
                Messages = new[] { message },
                SessionId = sid,
                UserId = userId
            }, cancellationToken).ConfigureAwait(false);

        return ToolJsonContext.Serialize(new
        {
            sessionId = sid,
            sourceMessageId = message.MessageId,
            entityCount = result.Entities.Count,
            factCount = result.Facts.Count,
            preferenceCount = result.Preferences.Count,
            relationshipCount = result.Relationships.Count,
            entities = result.Entities.Select(e => new { e.Name, e.Type, e.Confidence }),
            facts = result.Facts.Select(f => new { f.Subject, f.Predicate, f.Object, f.Confidence }),
            preferences = result.Preferences.Select(p => new { p.Category, p.PreferenceText, p.Confidence })
        });
    }

    [McpServerTool(Name = "memory_extract_session"), Description("Retroactively runs the extraction pipeline on all messages in a session and persists the resulting entities, facts, preferences, and relationships to long-term memory.")]
    public static async Task<string> MemoryExtractSession(
        IMemoryService memoryService,
        IOptions<McpServerOptions> options,
        [Description("Session identifier (optional, uses default if omitted)")] string? sessionId = null,
        [Description("Owner/user identifier (optional, R1). When set, extracted memories are owner-stamped and resolution is owner-scoped; null = stored as shared/global.")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var sid = sessionId ?? options.Value.DefaultSessionId;

        await memoryService.ExtractFromSessionAsync(sid, userId, cancellationToken).ConfigureAwait(false);

        return ToolJsonContext.Serialize(new
        {
            sessionId = sid,
            status = "extraction_complete"
        });
    }

    [McpServerTool(Name = "memory_generate_embeddings"), Description("Generates and persists embeddings for all nodes of the given label that currently have a null embedding. Supported labels: Entity, Fact, Preference.")]
    public static async Task<string> MemoryGenerateEmbeddings(
        IMemoryService memoryService,
        [Description("Node label to process: Entity, Fact, or Preference")] string nodeLabel,
        [Description("Number of nodes to process per batch (default: 100)")] int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var count = await memoryService.GenerateEmbeddingsBatchAsync(nodeLabel, batchSize, cancellationToken).ConfigureAwait(false);

        return ToolJsonContext.Serialize(new
        {
            nodeLabel,
            nodesUpdated = count
        });
    }
}
