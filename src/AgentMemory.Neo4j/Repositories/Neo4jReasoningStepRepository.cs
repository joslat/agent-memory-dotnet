using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

public sealed class Neo4jReasoningStepRepository : IReasoningStepRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jReasoningStepRepository> _logger;

    public Neo4jReasoningStepRepository(INeo4jTransactionRunner tx, ILogger<Neo4jReasoningStepRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<ReasoningStep> AddAsync(ReasoningStep step, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding reasoning step {Id} to trace {TraceId}", step.StepId, step.TraceId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"]          = step.StepId,
                ["traceId"]     = step.TraceId,
                ["stepNumber"]  = step.StepNumber,
                ["thought"]     = (object?)step.Thought,
                ["action"]      = (object?)step.Action,
                ["observation"] = (object?)step.Observation,
                ["metadata"]    = SerializeMetadata(step.Metadata)
            };

            var cursor = await runner.RunAsync(ReasoningQueries.AddStep, parameters);
            var record = await cursor.SingleAsync();
            var node = record["s"].As<INode>();

            if (step.Embedding is not null)
            {
                await runner.RunAsync(
                    ReasoningQueries.SetStepEmbedding,
                    new { id = step.StepId, embedding = step.Embedding.ToList() });
            }

            return MapToStep(node, step.Embedding);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ReasoningStep>> GetByTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting steps for trace {TraceId}", traceId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ReasoningQueries.GetStepsByTrace, new { traceId });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["s"].As<INode>();
                return MapToStep(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<ReasoningStep?> GetByIdAsync(string stepId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting reasoning step {Id}", stepId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ReasoningQueries.GetStepById, new { id = stepId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["s"].As<INode>();
            return MapToStep(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<int> LinkTouchedEntitiesAsync(
        string stepId, IReadOnlyList<string> entityIds, CancellationToken cancellationToken = default)
    {
        if (entityIds is null || entityIds.Count == 0)
            return 0;

        _logger.LogDebug("Linking {Count} touched entit(y/ies) to step {StepId}", entityIds.Count, stepId);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                ReasoningQueries.RecordTouchedEntitiesByIds,
                new { stepId, entityIds = entityIds.ToList() });
            var record = await cursor.SingleAsync();
            return (int)record["linked"].As<long>();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetTouchedEntityIdsAsync(
        string stepId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting touched entities for step {StepId}", stepId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ReasoningQueries.GetTouchedEntityIds, new { stepId });
            var records = await cursor.ToListAsync();
            return (IReadOnlyList<string>)records.Select(r => r["id"].As<string>()).ToList();
        }, cancellationToken);
    }

    private static ReasoningStep MapToStep(INode node, float[]? embedding) =>
        new()
        {
            StepId      = node["id"].As<string>(),
            TraceId     = node["trace_id"].As<string>(),
            StepNumber  = node["step_number"].As<int>(),
            Thought     = node.Properties.TryGetValue("thought", out var th) ? th.As<string>() : null,
            Action      = node.Properties.TryGetValue("action", out var act) ? act.As<string>() : null,
            Observation = node.Properties.TryGetValue("observation", out var obs) ? obs.As<string>() : null,
            Embedding   = embedding,
            TimestampUtc = node.Properties.TryGetValue("timestamp", out var ts)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(ts)
                                : null,
            Metadata    = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }
}
