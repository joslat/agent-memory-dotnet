using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

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

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildTraceParameters(trace);
            var cursor = await runner.RunAsync(ReasoningQueries.AddTrace, parameters);
            var record = await cursor.SingleAsync();
            var node = record["t"].As<INode>();

            // Only persist a real (non-empty) vector; a degraded empty embedding leaves it NULL.
            if (trace.TaskEmbedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    ReasoningQueries.SetTraceTaskEmbedding,
                    new { id = trace.TraceId, taskEmbedding = trace.TaskEmbedding.ToList() });
            }

            return MapToTrace(node, trace.TaskEmbedding);
        }, cancellationToken);
    }

    public async Task<ReasoningTrace> UpdateAsync(ReasoningTrace trace, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating reasoning trace {Id}", trace.TraceId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildTraceParameters(trace);
            var cursor = await runner.RunAsync(ReasoningQueries.UpdateTrace, parameters);
            var record = await cursor.SingleAsync();
            var node = record["t"].As<INode>();

            // Only persist a real (non-empty) vector; a degraded empty embedding leaves it NULL.
            if (trace.TaskEmbedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    ReasoningQueries.SetTraceTaskEmbedding,
                    new { id = trace.TraceId, taskEmbedding = trace.TaskEmbedding.ToList() });
            }

            return MapToTrace(node, trace.TaskEmbedding);
        }, cancellationToken);
    }

    public async Task<ReasoningTrace?> GetByIdAsync(string traceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting reasoning trace {Id}", traceId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ReasoningQueries.GetTraceById, new { id = traceId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["t"].As<INode>();
            return MapToTrace(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ReasoningTrace>> ListBySessionAsync(string sessionId, int limit = 10, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Listing reasoning traces for session {SessionId}, limit={Limit}, owner={Owner}", sessionId, limit, scope?.OwnerId);

        var cypher = ReasoningQueries.ListTracesBySession(hasOwner, includeShared);
        var parameters = new Dictionary<string, object> { ["sessionId"] = sessionId, ["limit"] = limit };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId!;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
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
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) task embedding has no semantic signal and
        // would throw a dimension mismatch at db.index.vector.queryNodes — short-circuit to an empty result.
        if (taskEmbedding is not { Length: > 0 }) return Array.Empty<(ReasoningTrace, double)>();
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = hasOwner
            ? Math.Max(limit * Neo4jFactRepository.OwnerOverFetchFactor, limit + Neo4jFactRepository.OwnerOverFetchFloor)
            : limit;

        _logger.LogDebug("Vector search reasoning traces, successFilter={Filter}, limit={Limit}, owner={Owner}",
            successFilter, limit, scope?.OwnerId);

        var cypher = ReasoningQueries.SearchByTaskVector(successFilter.HasValue, hasOwner, includeShared, topK);

        var parameters = new Dictionary<string, object>
        {
            ["embedding"] = taskEmbedding.ToList(),
            ["limit"]     = limit,
            ["minScore"]  = minScore
        };
        if (successFilter.HasValue) parameters["successFilter"] = successFilter.Value;
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId!;

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

    public async Task<IReadOnlyList<(ReasoningTrace Trace, double Score)>> SearchByTaskVectorAsOfAsync(
        float[] taskEmbedding,
        DateTimeOffset asOf,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) task embedding short-circuits to empty.
        if (taskEmbedding is not { Length: > 0 }) return Array.Empty<(ReasoningTrace, double)>();
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = hasOwner
            ? Math.Max(limit * Neo4jFactRepository.OwnerOverFetchFactor, limit + Neo4jFactRepository.OwnerOverFetchFloor)
            : limit;

        _logger.LogDebug("Temporal vector search reasoning traces as of {AsOf}, successFilter={Filter}, limit={Limit}, owner={Owner}",
            asOf, successFilter, limit, scope?.OwnerId);

        var cypher = ReasoningQueries.SearchByTaskVectorAsOf(successFilter.HasValue, hasOwner, includeShared, topK);

        var parameters = new Dictionary<string, object>
        {
            ["embedding"] = taskEmbedding.ToList(),
            ["limit"]     = limit,
            ["minScore"]  = minScore,
            ["asOf"]      = asOf.UtcDateTime.ToString("O")
        };
        if (successFilter.HasValue) parameters["successFilter"] = successFilter.Value;
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId!;

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
            await runner.RunAsync(
                ReasoningQueries.CreateInitiatedByRelationship,
                new { traceId, messageId });
        }, cancellationToken);
    }

    public async Task CreateConversationTraceRelationshipsAsync(string conversationId, string traceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_TRACE + IN_SESSION: Conversation {ConversationId} <-> Trace {TraceId}", conversationId, traceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                ReasoningQueries.CreateConversationTraceRelationships,
                new { conversationId, traceId });
        }, cancellationToken);
    }

    public async Task DeleteBySessionAsync(string sessionId, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        // null ownerId = the shared/global bucket ONLY (owner_id IS NULL), never "all owners" — a destructive
        // session-keyed delete must confine to exactly one R1 bucket (mirrors PruneSessionTracesAsync / the
        // FindDuplicate ownerIsShared idiom), so owner A can never clear owner B's traces.
        bool ownerIsShared = string.IsNullOrEmpty(ownerId);
        _logger.LogDebug("Deleting reasoning traces for session {SessionId}, owner={Owner}", sessionId, ownerId);

        var cypher = ReasoningQueries.DeleteBySession(ownerIsShared);
        var parameters = new Dictionary<string, object> { ["sessionId"] = sessionId };
        if (!ownerIsShared) parameters["ownerId"] = ownerId!;

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, parameters);
        }, cancellationToken);
    }

    public async Task<int> PruneSessionTracesAsync(string sessionId, int maxToKeep, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        // null ownerId = the shared/global bucket ONLY (owner_id IS NULL), never "all owners" — a destructive
        // prune must confine to exactly one R1 bucket (mirrors the FindDuplicate ownerIsShared idiom).
        bool ownerIsShared = string.IsNullOrEmpty(ownerId);
        _logger.LogDebug("Pruning reasoning traces for session {SessionId} to newest {Keep}, owner={Owner}",
            sessionId, maxToKeep, ownerId);

        var cypher = ReasoningQueries.PruneSessionTraces(ownerIsShared);
        var parameters = new Dictionary<string, object>
        {
            ["sessionId"] = sessionId,
            ["keep"] = maxToKeep
        };
        if (!ownerIsShared) parameters["ownerId"] = ownerId!;

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            return record["pruned"].As<int>();
        }, cancellationToken);
    }

    private static ReasoningTrace MapToTrace(INode node, float[]? taskEmbedding) =>
        new()
        {
            TraceId        = node["id"].As<string>(),
            SessionId      = node["session_id"].As<string>(),
            OwnerId        = node.Properties.TryGetValue("owner_id", out var oid) ? oid.As<string?>() : null,
            Task           = node["task"].As<string>(),
            TaskEmbedding  = taskEmbedding,
            Outcome        = node.Properties.TryGetValue("outcome", out var out_) ? out_.As<string>() : null,
            Success        = node.Properties.TryGetValue("success", out var succ) && succ is not null
                                ? succ.As<bool?>()
                                : null,
            StartedAtUtc   = Neo4jDateTimeHelper.ReadDateTimeOffset(node["started_at"]),
            CompletedAtUtc = node.Properties.TryGetValue("completed_at", out var ca)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(ca)
                                : null,
            Metadata       = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("task_embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    private static Dictionary<string, object?> BuildTraceParameters(ReasoningTrace trace) => new()
    {
        ["id"]          = trace.TraceId,
        ["sessionId"]   = trace.SessionId,
        ["ownerId"]     = (object?)trace.OwnerId,
        ["task"]        = trace.Task,
        ["outcome"]     = (object?)trace.Outcome,
        ["success"]     = (object?)trace.Success,
        ["startedAt"]   = trace.StartedAtUtc.ToString("O"),
        ["completedAt"] = (object?)(trace.CompletedAtUtc?.ToString("O")),
        ["metadata"]    = SerializeMetadata(trace.Metadata)
    };
}