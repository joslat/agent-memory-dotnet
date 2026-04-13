using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

public sealed class Neo4jReasoningTraceRepository : IReasoningTraceRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jReasoningTraceRepository> _logger;

    public Neo4jReasoningTraceRepository(INeo4jTransactionRunner tx, ILogger<Neo4jReasoningTraceRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<ReasoningTrace> AddAsync(ReasoningTrace trace, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding reasoning trace {Id}", trace.TraceId);

        const string cypher = @"
            CREATE (t:ReasoningTrace {
                id:           $id,
                sessionId:    $sessionId,
                task:         $task,
                outcome:      $outcome,
                success:      $success,
                startedAtUtc: $startedAtUtc,
                completedAtUtc: $completedAtUtc,
                metadata:     $metadata
            })
            RETURN t";

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildTraceParameters(trace);
            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            var node = record["t"].As<INode>();

            if (trace.TaskEmbedding is not null)
            {
                await runner.RunAsync(
                    "MATCH (t:ReasoningTrace {id: $id}) SET t.taskEmbedding = $taskEmbedding",
                    new { id = trace.TraceId, taskEmbedding = trace.TaskEmbedding.ToList() });
            }

            return MapToTrace(node, trace.TaskEmbedding);
        }, cancellationToken);
    }

    public async Task<ReasoningTrace> UpdateAsync(ReasoningTrace trace, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating reasoning trace {Id}", trace.TraceId);

        const string cypher = @"
            MATCH (t:ReasoningTrace {id: $id})
            SET
                t.task           = $task,
                t.outcome        = $outcome,
                t.success        = $success,
                t.startedAtUtc   = $startedAtUtc,
                t.completedAtUtc = $completedAtUtc,
                t.metadata       = $metadata
            RETURN t";

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildTraceParameters(trace);
            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            var node = record["t"].As<INode>();

            if (trace.TaskEmbedding is not null)
            {
                await runner.RunAsync(
                    "MATCH (t:ReasoningTrace {id: $id}) SET t.taskEmbedding = $taskEmbedding",
                    new { id = trace.TraceId, taskEmbedding = trace.TaskEmbedding.ToList() });
            }

            return MapToTrace(node, trace.TaskEmbedding);
        }, cancellationToken);
    }

    public async Task<ReasoningTrace?> GetByIdAsync(string traceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting reasoning trace {Id}", traceId);

        const string cypher = "MATCH (t:ReasoningTrace {id: $id}) RETURN t";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = traceId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["t"].As<INode>();
            return MapToTrace(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ReasoningTrace>> ListBySessionAsync(string sessionId, int limit = 10, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing reasoning traces for session {SessionId}, limit={Limit}", sessionId, limit);

        const string cypher = @"
            MATCH (t:ReasoningTrace {sessionId: $sessionId})
            RETURN t
            ORDER BY t.startedAtUtc DESC
            LIMIT $limit";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { sessionId, limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["t"].As<INode>();
                return MapToTrace(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(ReasoningTrace Trace, double Score)>> SearchByTaskVectorAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Vector search reasoning traces, successFilter={Filter}, limit={Limit}", successFilter, limit);

        var whereClause = successFilter.HasValue
            ? "WHERE score >= $minScore AND node.success = $successFilter"
            : "WHERE score >= $minScore";

        var cypher = $@"
            CALL db.index.vector.queryNodes('task_embedding_idx', $limit, $embedding)
            YIELD node, score
            {whereClause}
            RETURN node, score
            ORDER BY score DESC";

        var parameters = new Dictionary<string, object>
        {
            ["embedding"] = taskEmbedding.ToList(),
            ["limit"]     = limit,
            ["minScore"]  = minScore
        };
        if (successFilter.HasValue) parameters["successFilter"] = successFilter.Value;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToTrace(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    public async Task CreateInitiatedByRelationshipAsync(string traceId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating INITIATED_BY: Trace {TraceId} -> Message {MessageId}", traceId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (t:ReasoningTrace {id: $traceId}), (m:Message {id: $messageId})
                MERGE (t)-[:INITIATED_BY]->(m)",
                new { traceId, messageId });
        }, cancellationToken);
    }

    public async Task CreateConversationTraceRelationshipsAsync(string conversationId, string traceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_TRACE + IN_SESSION: Conversation {ConversationId} <-> Trace {TraceId}", conversationId, traceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (c:Conversation {id: $conversationId}), (t:ReasoningTrace {id: $traceId})
                MERGE (c)-[:HAS_TRACE]->(t)
                MERGE (t)-[:IN_SESSION]->(c)",
                new { conversationId, traceId });
        }, cancellationToken);
    }

    private static ReasoningTrace MapToTrace(INode node, float[]? taskEmbedding) =>
        new()
        {
            TraceId        = node["id"].As<string>(),
            SessionId      = node["sessionId"].As<string>(),
            Task           = node["task"].As<string>(),
            TaskEmbedding  = taskEmbedding,
            Outcome        = node.Properties.TryGetValue("outcome", out var out_) ? out_.As<string>() : null,
            Success        = node.Properties.TryGetValue("success", out var succ) && succ is not null
                                ? succ.As<bool?>()
                                : null,
            StartedAtUtc   = DateTimeOffset.Parse(node["startedAtUtc"].As<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
            CompletedAtUtc = node.Properties.TryGetValue("completedAtUtc", out var ca) && ca.As<string>() is { } caStr && !string.IsNullOrEmpty(caStr)
                                ? DateTimeOffset.Parse(caStr, null, System.Globalization.DateTimeStyles.RoundtripKind)
                                : null,
            Metadata       = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("taskEmbedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    private static Dictionary<string, object?> BuildTraceParameters(ReasoningTrace trace) => new()
    {
        ["id"]             = trace.TraceId,
        ["sessionId"]      = trace.SessionId,
        ["task"]           = trace.Task,
        ["outcome"]        = (object?)trace.Outcome,
        ["success"]        = (object?)trace.Success,
        ["startedAtUtc"]   = trace.StartedAtUtc.ToString("O"),
        ["completedAtUtc"] = (object?)(trace.CompletedAtUtc?.ToString("O")),
        ["metadata"]       = SerializeMetadata(trace.Metadata)
    };

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}