using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed class Neo4jToolCallRepository : IToolCallRepository
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

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildToolCallParameters(toolCall);
            var cursor = await runner.RunAsync(ToolCallQueries.Add, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            var tcNode = record["tc"].As<INode>();

            // Create INSTANCE_OF relationship to a Tool node (auto-created on first encounter)
            await runner.RunAsync(
                ToolCallQueries.UpsertToolInstance,
                new { id = toolCall.ToolCallId, toolName = toolCall.ToolName,
                      status = toolCall.Status.ToString().ToLowerInvariant(),
                      durationMs = (object?)toolCall.DurationMs,
                      description = (object?)toolCall.Description }).ConfigureAwait(false);

            return MapToToolCall(tcNode);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolCall?> UpdateAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating tool call {Id}", toolCall.ToolCallId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildToolCallParameters(toolCall);
            var cursor = await runner.RunAsync(ToolCallQueries.Update, parameters).ConfigureAwait(false);
            // The Update Cypher is MATCH ... RETURN, so a concurrently-cleared grandchild (a session clear /
            // trace prune between a read and this write) yields no row. Return null instead of throwing a
            // sequence-empty exception — the same clean not-found contract as IReasoningTraceRepository.UpdateAsync.
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 ? MapToToolCall(records[0]["tc"].As<INode>()) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolCall>> GetByStepAsync(string stepId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting tool calls for step {StepId}", stepId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ToolCallQueries.GetByStep, new { stepId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r => MapToToolCall(r["tc"].As<INode>())).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolCall?> GetByIdAsync(string toolCallId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting tool call {Id}", toolCallId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ToolCallQueries.GetById, new { id = toolCallId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            return MapToToolCall(records[0]["tc"].As<INode>());
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ToolCall MapToToolCall(INode node) =>
        new()
        {
            ToolCallId    = node["id"].As<string>(),
            StepId        = node["step_id"].As<string>(),
            ToolName      = node["tool_name"].As<string>(),
            ArgumentsJson = node["arguments"].As<string>(),
            ResultJson    = node.Properties.TryGetValue("result", out var rj) ? rj.As<string>() : null,
            Status        = Enum.Parse<ToolCallStatus>(node["status"].As<string>(), ignoreCase: true),
            DurationMs    = node.Properties.TryGetValue("duration_ms", out var dm) && dm is not null
                                ? dm.As<long?>()
                                : null,
            Error         = node.Properties.TryGetValue("error", out var err) ? err.As<string>() : null,
            TimestampUtc  = node.Properties.TryGetValue("timestamp", out var ts)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(ts)
                                : null,
            Metadata      = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static Dictionary<string, object?> BuildToolCallParameters(ToolCall tc) => new()
    {
        ["id"]         = tc.ToolCallId,
        ["stepId"]     = tc.StepId,
        ["toolName"]   = tc.ToolName,
        ["arguments"]  = tc.ArgumentsJson,
        ["result"]     = (object?)tc.ResultJson,
        ["status"]     = tc.Status.ToString().ToLowerInvariant(),
        ["durationMs"] = (object?)tc.DurationMs,
        ["error"]      = (object?)tc.Error,
        ["metadata"]   = SerializeMetadata(tc.Metadata)
    };

    public async Task CreateTriggeredByRelationshipAsync(
        string toolCallId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating TRIGGERED_BY: ToolCall {ToolCallId} -> Message {MessageId}", toolCallId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                ToolCallQueries.CreateTriggeredByRelationship,
                new { toolCallId, messageId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolCallStats>> GetStatsAsync(
        string? toolName = null,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Aggregating tool stats, tool={Tool}, owner={Owner}", toolName, scope?.OwnerId);

        var cypher = ToolCallQueries.GetStats(hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["toolName"] = toolName };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId!;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var totalCalls = checked((int)r["total_calls"].As<long>());
                var successful = checked((int)r["successful_calls"].As<long>());
                var failed = checked((int)r["failed_calls"].As<long>());
                var totalDurationMs = r["total_duration_ms"].As<long?>() ?? 0;
                return new ToolCallStats(
                    r["name"].As<string>(),
                    totalCalls,
                    successful,
                    failed,
                    totalCalls > 0 ? (double)successful / totalCalls : 0.0,
                    totalCalls > 0 ? (double)totalDurationMs / totalCalls : null);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
}