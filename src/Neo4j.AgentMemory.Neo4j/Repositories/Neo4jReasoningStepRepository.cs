using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

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

        const string cypher = @"
            MATCH (t:ReasoningTrace {id: $traceId})
            CREATE (s:ReasoningStep {
                id:          $id,
                trace_id:    $traceId,
                step_number: $stepNumber,
                thought:     $thought,
                action:      $action,
                observation: $observation,
                metadata:    $metadata
            })
            CREATE (t)-[:HAS_STEP {order: $stepNumber}]->(s)
            RETURN s";

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

            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            var node = record["s"].As<INode>();

            if (step.Embedding is not null)
            {
                await runner.RunAsync(
                    "MATCH (s:ReasoningStep {id: $id}) SET s.embedding = $embedding",
                    new { id = step.StepId, embedding = step.Embedding.ToList() });
            }

            return MapToStep(node, step.Embedding);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ReasoningStep>> GetByTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting steps for trace {TraceId}", traceId);

        const string cypher = @"
            MATCH (t:ReasoningTrace {id: $traceId})-[:HAS_STEP]->(s:ReasoningStep)
            RETURN s
            ORDER BY s.step_number";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { traceId });
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

        const string cypher = "MATCH (s:ReasoningStep {id: $id}) RETURN s";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = stepId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["s"].As<INode>();
            return MapToStep(node, ReadEmbedding(node));
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
            Metadata    = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}
