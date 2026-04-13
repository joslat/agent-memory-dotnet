using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

public sealed class Neo4jToolCallRepository : IToolCallRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jToolCallRepository> _logger;

    public Neo4jToolCallRepository(INeo4jTransactionRunner tx, ILogger<Neo4jToolCallRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<ToolCall> AddAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding tool call {Id} to step {StepId}", toolCall.ToolCallId, toolCall.StepId);

        const string cypher = @"
            MATCH (s:ReasoningStep {id: $stepId})
            CREATE (tc:ToolCall {
                id:            $id,
                stepId:        $stepId,
                toolName:      $toolName,
                argumentsJson: $argumentsJson,
                resultJson:    $resultJson,
                status:        $status,
                durationMs:    $durationMs,
                error:         $error,
                metadata:      $metadata
            })
            CREATE (s)-[:USED_TOOL]->(tc)
            RETURN tc";

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildToolCallParameters(toolCall);
            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            var tcNode = record["tc"].As<INode>();

            // Create CALLS relationship to a Tool node (auto-created on first encounter)
            await runner.RunAsync(@"
                MATCH (tc:ToolCall {id: $id})
                MERGE (tool:Tool {name: $toolName})
                ON CREATE SET tool.createdAtUtc = $now
                MERGE (tc)-[:CALLS]->(tool)
                SET tool.totalCalls = COALESCE(tool.totalCalls, 0) + 1",
                new { id = toolCall.ToolCallId, toolName = toolCall.ToolName, now = DateTimeOffset.UtcNow.ToString("O") });

            return MapToToolCall(tcNode);
        }, cancellationToken);
    }

    public async Task<ToolCall> UpdateAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating tool call {Id}", toolCall.ToolCallId);

        const string cypher = @"
            MATCH (tc:ToolCall {id: $id})
            SET
                tc.toolName      = $toolName,
                tc.argumentsJson = $argumentsJson,
                tc.resultJson    = $resultJson,
                tc.status        = $status,
                tc.durationMs    = $durationMs,
                tc.error         = $error,
                tc.metadata      = $metadata
            RETURN tc";

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildToolCallParameters(toolCall);
            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            return MapToToolCall(record["tc"].As<INode>());
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ToolCall>> GetByStepAsync(string stepId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting tool calls for step {StepId}", stepId);

        const string cypher = @"
            MATCH (s:ReasoningStep {id: $stepId})-[:USED_TOOL]->(tc:ToolCall)
            RETURN tc
            ORDER BY tc.toolName";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { stepId });
            var records = await cursor.ToListAsync();
            return records.Select(r => MapToToolCall(r["tc"].As<INode>())).ToList();
        }, cancellationToken);
    }

    public async Task<ToolCall?> GetByIdAsync(string toolCallId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting tool call {Id}", toolCallId);

        const string cypher = "MATCH (tc:ToolCall {id: $id}) RETURN tc";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = toolCallId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            return MapToToolCall(records[0]["tc"].As<INode>());
        }, cancellationToken);
    }

    private static ToolCall MapToToolCall(INode node) =>
        new()
        {
            ToolCallId    = node["id"].As<string>(),
            StepId        = node["stepId"].As<string>(),
            ToolName      = node["toolName"].As<string>(),
            ArgumentsJson = node["argumentsJson"].As<string>(),
            ResultJson    = node.Properties.TryGetValue("resultJson", out var rj) ? rj.As<string>() : null,
            Status        = Enum.Parse<ToolCallStatus>(node["status"].As<string>()),
            DurationMs    = node.Properties.TryGetValue("durationMs", out var dm) && dm is not null
                                ? dm.As<long?>()
                                : null,
            Error         = node.Properties.TryGetValue("error", out var err) ? err.As<string>() : null,
            Metadata      = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static Dictionary<string, object?> BuildToolCallParameters(ToolCall tc) => new()
    {
        ["id"]            = tc.ToolCallId,
        ["stepId"]        = tc.StepId,
        ["toolName"]      = tc.ToolName,
        ["argumentsJson"] = tc.ArgumentsJson,
        ["resultJson"]    = (object?)tc.ResultJson,
        ["status"]        = tc.Status.ToString(),
        ["durationMs"]    = (object?)tc.DurationMs,
        ["error"]         = (object?)tc.Error,
        ["metadata"]      = SerializeMetadata(tc.Metadata)
    };

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();

    public async Task CreateTriggeredByRelationshipAsync(
        string toolCallId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating TRIGGERED_BY: ToolCall {ToolCallId} -> Message {MessageId}", toolCallId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (tc:ToolCall {id: $toolCallId}), (m:Message {id: $messageId})
                MERGE (tc)-[:TRIGGERED_BY]->(m)",
                new { toolCallId, messageId });
        }, cancellationToken);
    }
}