using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.McpServer.Tools;

/// <summary>
/// Advanced memory tools: record tool calls, export graph, find duplicates, extract and persist.
/// </summary>
[McpServerToolType]
public sealed class AdvancedMemoryTools
{
    [McpServerTool(Name = "memory_record_tool_call"), Description("Records a tool call for a reasoning trace step. Associates a tool invocation with an existing reasoning step.")]
    public static async Task<string> MemoryRecordToolCall(
        IReasoningMemoryService reasoningMemory,
        [Description("The reasoning step ID this tool call belongs to")] string stepId,
        [Description("Name of the tool that was called")] string toolName,
        [Description("JSON-serialized arguments passed to the tool")] string input,
        [Description("JSON-serialized result from the tool (optional)")] string? output = null,
        [Description("Status of the call: Pending, Success, Error, or Cancelled (default: Success)")] string status = "Success",
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ToolCallStatus>(status, ignoreCase: true, out var toolStatus))
            toolStatus = ToolCallStatus.Success;

        var toolCall = await reasoningMemory.RecordToolCallAsync(
            stepId, toolName, input, output, toolStatus,
            cancellationToken: cancellationToken);

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

        var nodeQuery = sessionId is null
            ? "MATCH (n) RETURN labels(n) AS labels, properties(n) AS props LIMIT $limit"
            : "MATCH (n) WHERE n.sessionId = $sessionId RETURN labels(n) AS labels, properties(n) AS props LIMIT $limit";

        var relQuery = sessionId is null
            ? "MATCH (a)-[r]->(b) RETURN a.entityId AS fromId, type(r) AS relType, b.entityId AS toId, properties(r) AS relProps LIMIT $limit"
            : "MATCH (a)-[r]->(b) WHERE a.sessionId = $sessionId AND b.sessionId = $sessionId RETURN a.entityId AS fromId, type(r) AS relType, b.entityId AS toId, properties(r) AS relProps LIMIT $limit";

        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = (long)limit,
            ["sessionId"] = (object?)sessionId
        };

        var nodes = await graphQueryService.QueryAsync(nodeQuery, parameters, cancellationToken);
        var relationships = await graphQueryService.QueryAsync(relQuery, parameters, cancellationToken);

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

        const string query = """
            MATCH (a:Entity), (b:Entity)
            WHERE elementId(a) < elementId(b)
              AND a.entityId <> b.entityId
              AND NOT (a)-[:SAME_AS]-(b)
              AND (toLower(a.name) CONTAINS toLower(b.name)
                   OR toLower(b.name) CONTAINS toLower(a.name))
            WITH a, b,
                 CASE WHEN size(a.name) >= size(b.name)
                      THEN toFloat(size(b.name)) / size(a.name)
                      ELSE toFloat(size(a.name)) / size(b.name)
                 END AS similarity
            WHERE similarity >= $threshold
            RETURN a.entityId AS entityAId, a.name AS nameA, a.type AS typeA,
                   b.entityId AS entityBId, b.name AS nameB, b.type AS typeB,
                   similarity
            ORDER BY similarity DESC
            LIMIT $limit
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["threshold"] = threshold,
            ["limit"] = (long)limit
        };

        var results = await graphQueryService.QueryAsync(query, parameters, cancellationToken);

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
                SessionId = sid
            }, cancellationToken);

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
}
