using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed class Neo4jReasoningTraceRepository : IReasoningTraceRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly bool _rescueShortOwnerResults;
    /// <summary>2.13: skip a futile widened probe + scan for an owner holding nothing.</summary>
    private readonly bool _skipEscalationWhenOwnerHasNoRows;
    /// <summary>Scores this owner's OWN traces directly, bypassing the global vector index.</summary>
    /// <remarks>
    /// Extracted because two conditions reach it -- an empty scoped result, and (opt-in) a short one
    /// -- and two copies of a fallback drift.
    /// </remarks>
    private async Task<List<(ReasoningTrace, double)>> OwnerScopedScanAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope scope,
        bool includeShared,
        bool? successFilter,
        bool? proceduresOnly,
        CancellationToken cancellationToken) =>
        await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                ReasoningQueries.SearchByTaskVectorOwnerScopedFallback(
                    successFilter.HasValue, includeShared, proceduresOnly),
                new Dictionary<string, object?>
                {
                    ["embedding"] = queryEmbedding.ToList(),
                    ["limit"] = limit,
                    ["minScore"] = minScore,
                    ["ownerId"] = scope.OwnerId,
                }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToTrace(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

    private readonly ILogger<Neo4jReasoningTraceRepository> _logger;

    public Neo4jReasoningTraceRepository(
        INeo4jTransactionRunner tx,
        ILogger<Neo4jReasoningTraceRepository> logger,
        IOptions<MemoryOptions>? memoryOptions = null)
    {
        _rescueShortOwnerResults = memoryOptions?.Value.RescueShortOwnerResults ?? false;
        _skipEscalationWhenOwnerHasNoRows =
            memoryOptions?.Value.SkipEscalationWhenOwnerHasNoRows ?? false;
        _tx = tx;
        _logger = logger;
    }

    public async Task<ReasoningTrace> AddAsync(ReasoningTrace trace, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding reasoning trace {Id}", trace.TraceId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildTraceParameters(trace);
            var cursor = await runner.RunAsync(ReasoningQueries.AddTrace, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            var node = record["t"].As<INode>();

            // Only persist a real (non-empty) vector; a degraded empty embedding leaves it NULL.
            if (trace.TaskEmbedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    ReasoningQueries.SetTraceTaskEmbedding,
                    new { id = trace.TraceId, taskEmbedding = trace.TaskEmbedding.ToList() }).ConfigureAwait(false);
            }

            return MapToTrace(node, trace.TaskEmbedding);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReasoningTrace?> UpdateAsync(ReasoningTrace trace, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating reasoning trace {Id}", trace.TraceId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = BuildTraceParameters(trace);
            var cursor = await runner.RunAsync(ReasoningQueries.UpdateTrace, parameters).ConfigureAwait(false);
            // The UpdateTrace MATCH returns no rows if the trace was concurrently deleted (session clear /
            // retention prune) between the caller's read and this write. Return null instead of letting
            // SingleAsync throw an opaque "sequence contains no elements" (R6-E).
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["t"].As<INode>();

            // Only persist a real (non-empty) vector; a degraded empty embedding leaves it NULL.
            if (trace.TaskEmbedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    ReasoningQueries.SetTraceTaskEmbedding,
                    new { id = trace.TraceId, taskEmbedding = trace.TaskEmbedding.ToList() }).ConfigureAwait(false);
            }

            return MapToTrace(node, trace.TaskEmbedding);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReasoningTrace?> GetByIdAsync(string traceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting reasoning trace {Id}", traceId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ReasoningQueries.GetTraceById, new { id = traceId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["t"].As<INode>();
            return MapToTrace(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
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
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["t"].As<INode>();
                return MapToTrace(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<ReasoningTrace>> ListAllAsync(int limit = 50, int offset = 0, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        // Predictable failures for invalid paging input, and no wraparound on the N+1 fetch below.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Listing all reasoning traces, limit={Limit}, offset={Offset}, owner={Owner}", limit, offset, scope?.OwnerId);

        var cypher = ReasoningQueries.ListAllTraces(hasOwner, includeShared);
        // Fetch one extra row (N+1) to set HasNextPage without a separate COUNT round-trip.
        var parameters = new Dictionary<string, object> { ["offset"] = offset, ["limit"] = checked(limit + 1) };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId!;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var traces = records.Take(limit).Select(r =>
            {
                var node = r["t"].As<INode>();
                return MapToTrace(node, ReadEmbedding(node));
            }).ToList();
            return new PagedResult<ReasoningTrace>(traces, records.Count > limit);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<(ReasoningTrace Trace, double Score)>> SearchByTaskVectorAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        SearchByTaskVectorAsync(
            taskEmbedding, proceduresOnly: null, successFilter, limit, minScore, scope, cancellationToken);

    public async Task<IReadOnlyList<(ReasoningTrace Trace, double Score)>> SearchByTaskVectorAsync(
        float[] taskEmbedding,
        bool? proceduresOnly,
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
        int topK = OwnerVectorOverFetch.InitialTopK(limit, hasOwner);

        // Recall-yield signal, mirroring Neo4jFactRepository.SearchByVectorAsync. The vector index is
        // global, so the owner filter is a POST-filter on a top-K drawn from every tenant (see
        // OwnerVectorOverFetch): how much of that budget actually reached the querying owner was
        // measurable on the fact path only, and invisible here. Started AFTER the degraded-embedding
        // short-circuit above, so a search that never reached the index does not publish a zero-yield
        // reading it never earned.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.trace_vector");
        _logger.LogDebug("Vector search reasoning traces, successFilter={Filter}, limit={Limit}, owner={Owner}",
            successFilter, limit, scope?.OwnerId);

        async Task<List<(ReasoningTrace, double)>> QueryAsync(int width, CancellationToken ct)
        {
            var cypher = ReasoningQueries.SearchByTaskVector(
                successFilter.HasValue, hasOwner, includeShared, width, proceduresOnly);

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
                var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
                var records = await cursor.ToListAsync().ConfigureAwait(false);
                return records.Select(r =>
                {
                    var node  = r["node"].As<INode>();
                    var score = r["score"].As<double>();
                    return (MapToTrace(node, ReadEmbedding(node)), score);
                }).ToList();
            }, ct).ConfigureAwait(false) ?? [];
        }

        var results = await QueryAsync(topK, cancellationToken).ConfigureAwait(false);

        // The empty-result rescue, matching the fact, entity and preference paths. Traces run the same
        // global-index-then-post-filter shape, and the entity measurement showed that shape returning
        // 0 of an owner's 4 rows against 500 more-similar foreign rows until this retry was added.
        //
        // NOTE the interaction with successFilter: a filtered search can legitimately return nothing
        // because no trace matched the filter, not because of crowding. Escalating still costs only
        // one wider query and cannot invent a match, so the retry is issued either way rather than
        // guessing which cause applied.
        int? escalatedTopK = null;
        if (OwnerVectorOverFetch.ShouldEscalate(results.Count, hasOwner)
            && await ShouldClimbLadderAsync(scope, includeShared, cancellationToken).ConfigureAwait(false))
        {
            var widened = OwnerVectorOverFetch.EscalatedTopK(topK);
            if (widened > topK)
            {
                _logger.LogDebug(
                    "Owner-scoped trace vector search returned nothing at topK={TopK}; retrying at {Widened}.",
                    topK, widened);
                escalatedTopK = widened;
                results = await QueryAsync(widened, cancellationToken).ConfigureAwait(false);
            }

            // LAST RESORT, as on the fact/entity/preference paths: EscalatedTopK is capped, so beyond
            // that many more-similar foreign rows no widening reaches the owner's. Bounded by one
            // owner's traces rather than by the corpus, and reached only when both prior passes
            // returned nothing. The success filter is carried through, so a filtered search that
            // genuinely matches nothing still returns nothing -- and so is proceduresOnly, which is
            // the filter most likely to reach this branch: a corpus with no promoted procedures makes
            // the indexed pass return zero by construction, so an unfiltered rescue would answer a
            // procedure lookup with an episode.
            if (results.Count == 0)
            {
                _logger.LogDebug(
                    "Owner-scoped trace vector search still empty after widening; falling back to a "
                    + "scoped similarity scan.");
                var fallbackParameters = new Dictionary<string, object>
                {
                    ["embedding"] = taskEmbedding.ToList(),
                    ["limit"] = limit,
                    ["minScore"] = minScore,
                    ["ownerId"] = scope!.OwnerId!,
                };
                if (successFilter.HasValue) fallbackParameters["successFilter"] = successFilter.Value;
                results = await OwnerScopedScanAsync(
                    taskEmbedding, limit, minScore, scope!, includeShared, successFilter,
                    proceduresOnly, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (_rescueShortOwnerResults
                 && OwnerVectorOverFetch.ShouldRescueShortResult(results.Count, limit, hasOwner))
        {
            // 2.12, the same rescue the fact path carries. A host enabling RescueShortOwnerResults
            // would otherwise get it on facts and silently not on traces -- the "setting only some
            // components respect" shape this project has found twice.
            _logger.LogDebug(
                "Owner-scoped trace vector search returned {Returned} of {Limit}; rescuing with "
                + "a scoped similarity scan.", results.Count, limit);
            var scanned = await OwnerScopedScanAsync(
                taskEmbedding, limit, minScore, scope!, includeShared, successFilter,
                proceduresOnly, cancellationToken).ConfigureAwait(false);
            if (scanned.Count > results.Count) results = scanned;
        }

        // Explicit null check rather than `activity?.SetTag(...)` per AgentMemoryDiagnostics' remarks: the
        // tag block exists purely to produce telemetry, so it is skipped whole when nobody is listening.
        // Success path only — a search that threw measured nothing, and tagging it `returned = 0` would be
        // indistinguishable from a genuine total-starvation reading.
        if (activity is not null)
        {
            TagVectorYield(
                activity, hasOwner, limit, escalatedTopK ?? topK, results.Count, successFilter,
                escalated: escalatedTopK is not null, requestedTopK: topK);
        }

        return results;
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
        int topK = OwnerVectorOverFetch.InitialTopK(limit, hasOwner);

        // Same yield signal as the live path, under its own span name: a point-in-time recall and a live
        // one answer different questions, and folding them into one name would make either unreadable.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.trace_vector_as_of");
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

        var results = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToTrace(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        if (activity is not null)
            TagVectorYield(activity, hasOwner, limit, topK, results.Count, successFilter);

        return results;
    }

    /// <summary>
    /// The recall-yield tags shared by both reasoning-trace vector paths.
    /// </summary>
    /// <remarks>
    /// <c>owner_scoped</c> separates the two populations: an unscoped search has no post-filter and so no
    /// starvation to report, and folding them together would dilute the signal. <c>limit</c> is the other
    /// half of the denominator — <c>returned</c> is capped by the Cypher's <c>LIMIT $limit</c>.
    /// <para>
    /// <c>effective_topk</c> is the width that actually produced <c>returned</c>. On these paths it always
    /// equals <c>requested_topk</c> because exactly one query is issued, but it is emitted anyway so a
    /// consumer computing <c>returned / effective_topk</c> across every vector span gets a correct ratio
    /// without having to know which sources can widen and which cannot.
    /// </para>
    /// <para>
    /// <b>The outcome filter is reported because it changes what a yield means.</b> With
    /// <c>node.success = $successFilter</c> in the WHERE clause the same global top-K is cut twice, and
    /// "2 of 60" from an outcome-filtered search is not the same reading as "2 of 60" from an unfiltered
    /// one — a consumer cannot tell the two apart from the numbers alone. It is split into two tags on
    /// purpose: the filter is three-state (<c>null</c>/<c>true</c>/<c>false</c>), so
    /// <c>success_filtered</c> answers "was the candidate set cut?" and <c>success_filter</c> — present
    /// only when it was — answers "which way?". Collapsing them into one bool would make a failures-only
    /// search read as an unfiltered one.
    /// </para>
    /// <para>
    /// <c>escalated</c> is emitted as <c>false</c>, not omitted. Neither trace search retries an empty
    /// scoped result at a wider topK, and an earlier version of this remark argued that emitting
    /// <c>false</c> would dilute any measure of how often the rescue fires. That reasoning was
    /// overturned when the same argument, applied site by site, produced <b>three different tag
    /// vocabularies</b> across eight searches: an omitted tag is indistinguishable from a site that
    /// emits no telemetry at all. <c>false</c> means "no second pass ran"; an escalation count filters
    /// on <c>escalated = true</c> and is unaffected.
    /// </para>
    /// <para>
    /// Still absent, and this part stands: <c>escalated_topk</c> when no second query ran, and the true
    /// pre-filter candidate count — the owner filter, the success filter and LIMIT all run inside
    /// Cypher, so that number never reaches this process and a plausible guess would be worse than
    /// silence.
    /// </para>
    /// </remarks>
    private static void TagVectorYield(
        Activity activity, bool hasOwner, int limit, int topK, int returned, bool? successFilter,
        bool escalated = false, int? requestedTopK = null)
    {
        activity.SetTag("memory.vector.owner_scoped", hasOwner);
        activity.SetTag("memory.vector.limit", limit);
        activity.SetTag("memory.vector.requested_topk", requestedTopK ?? topK);
        activity.SetTag("memory.vector.effective_topk", topK);
        // Emitted by EVERY vector-recall span, including paths that never escalate. The three
        // conventions this replaces made the telemetry unqueryable: a consumer computing
        // returned/effective_topk had to know which sites emit it, and an omitted "escalated"
        // is indistinguishable from a site that emits no telemetry at all. False here means
        // "no second pass ran", which is exactly what a consumer counting escalations needs.
        activity.SetTag("memory.vector.escalated", escalated);
        // Absent, never defaulted, when no second pass ran - a width nobody asked for is not a
        // measurement. The live path can now escalate, so a hardcoded false would be fabricated.
        if (escalated) activity.SetTag("memory.vector.escalated_topk", topK);
        activity.SetTag("memory.vector.returned", returned);
        activity.SetTag("memory.vector.success_filtered", successFilter.HasValue);
        // Absent, never defaulted, when no filter was applied: an outcome nobody filtered on is not a
        // measurement, and `false` here already means "failed traces only".
        if (successFilter is { } filter) activity.SetTag("memory.vector.success_filter", filter);
    }

    public async Task CreateInitiatedByRelationshipAsync(string traceId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating INITIATED_BY: Trace {TraceId} -> Message {MessageId}", traceId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                ReasoningQueries.CreateInitiatedByRelationship,
                new { traceId, messageId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateConversationTraceRelationshipsAsync(string conversationId, string traceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_TRACE + IN_SESSION: Conversation {ConversationId} <-> Trace {TraceId}", conversationId, traceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                ReasoningQueries.CreateConversationTraceRelationships,
                new { conversationId, traceId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
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
            await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
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
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return record["pruned"].As<int>();
        }, cancellationToken).ConfigureAwait(false);
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
            // Absent property == Episode. Every trace written before trace_kind existed reads back as
            // an episode, which is what it is -- never as an unknown that a caller has to handle.
            Kind           = node.Properties.TryGetValue("trace_kind", out var tk)
                                && string.Equals(tk?.As<string?>(), "procedure", StringComparison.Ordinal)
                                    ? TraceKind.Procedure
                                    : TraceKind.Episode,
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
        ["metadata"]    = SerializeMetadata(trace.Metadata),
        // Written as a lowercase string rather than an int so the stored value is readable in a Cypher
        // console and stable if the enum is ever reordered -- an ordinal would silently re-point every
        // existing node at a different meaning.
        ["traceKind"]   = trace.Kind == TraceKind.Procedure ? "procedure" : "episode"
    };

    /// <summary>
    /// Whether the escalation ladder can possibly help this owner (2.13).
    /// </summary>
    /// <remarks>
    /// Returns <see langword="true"/> unless the option is on AND the owner provably holds no rows of
    /// this label. Defaulting to "escalate" is the safe direction: a probe that wrongly reported empty
    /// would skip a rescue that would have worked, and a silent recall loss costs far more than one
    /// avoided query.
    /// </remarks>
    private async Task<bool> ShouldClimbLadderAsync(
        MemoryScope? scope, bool includeShared, CancellationToken cancellationToken)
    {
        if (!_skipEscalationWhenOwnerHasNoRows || scope?.OwnerId is null) return true;

        var present = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                OwnerRowExistence.Any("ReasoningTrace", includeShared),
                new { ownerId = scope.OwnerId }).ConfigureAwait(false);
            return (await cursor.ToListAsync().ConfigureAwait(false)).Count > 0;
        }, cancellationToken).ConfigureAwait(false);

        if (!present)
        {
            _logger.LogDebug(
                "Owner {Owner} holds no ReasoningTrace rows; skipping the escalation ladder (2.13).",
                scope.OwnerId);
        }

        return present;
    }
}