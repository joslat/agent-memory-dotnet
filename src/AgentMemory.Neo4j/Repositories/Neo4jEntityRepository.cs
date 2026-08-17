using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jEntityRepository : IEntityRepository, IUpsertPersistsProvenance,
    IBatchMemoryRepository<Entity>, IFusedBatchMemoryRepository<Entity>
{
    private readonly IWorkingMemoryService? _workingMemory;
    private readonly WorkingMemoryOptions _workingMemoryOptions;


    private readonly INeo4jTransactionRunner _tx;
    private readonly bool _rescueShortOwnerResults;
    /// <summary>2.13: skip a futile widened probe + scan for an owner holding nothing.</summary>
    private readonly bool _skipEscalationWhenOwnerHasNoRows;
    /// <summary>Payload projection: drop the ~3 KB vector nothing on the recall path reads.</summary>
    private readonly bool _omitEmbeddingsFromRecall;
    private readonly ILogger<Neo4jEntityRepository> _logger;
    private readonly MemoryRankingOptions _ranking;
    private readonly MemoryDecayOptions _decay;
    /// <summary>Scores this owner's OWN entitys directly, bypassing the global vector index.</summary>
    /// <remarks>
    /// Extracted because two conditions reach it -- an empty scoped result, and (opt-in) a short one
    /// -- and two copies of a fallback drift.
    /// </remarks>
    private async Task<List<(Entity, double)>> OwnerScopedScanAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope scope,
        bool includeShared,
        CancellationToken cancellationToken) =>
        await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                EntityQueries.SearchByVectorOwnerScopedFallback(includeShared),
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
                return (MapToEntity(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

    private readonly IMemoryRankingContext? _rankingContext;

    public Neo4jEntityRepository(
        INeo4jTransactionRunner tx,
        ILogger<Neo4jEntityRepository> logger,
        IOptions<MemoryRankingOptions>? ranking = null,
        IOptions<MemoryDecayOptions>? decay = null,
        IMemoryRankingContext? rankingContext = null,
        IOptions<MemoryOptions>? memoryOptions = null,
        // 30.4b. Optional, mirroring every other working-memory injection point: a host that never
        // registered the tier keeps the exact previous construction shape.
        IWorkingMemoryService? workingMemory = null)
    {
        _workingMemory = workingMemory;
        _workingMemoryOptions = memoryOptions?.Value.WorkingMemory ?? new WorkingMemoryOptions();
        _rescueShortOwnerResults = memoryOptions?.Value.RescueShortOwnerResults ?? false;
        _skipEscalationWhenOwnerHasNoRows =
            memoryOptions?.Value.SkipEscalationWhenOwnerHasNoRows ?? false;
        _omitEmbeddingsFromRecall = memoryOptions?.Value.OmitEmbeddingsFromRecall ?? false;
        _tx = tx;
        _logger = logger;
        _ranking = ranking?.Value ?? MemoryRankingOptions.Default;
        _decay = decay?.Value ?? MemoryDecayOptions.Default;
        _rankingContext = rankingContext;
    }

    public async Task<Entity> UpsertAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting entity {Id} ({Name})", entity.EntityId, entity.Name);

        return await _tx.WriteAsync(async runner =>
        {
            // L9. name and canonical_name are range-indexed, and nothing upstream bounds their
            // length - the only rule in the codebase is a MINIMUM. Checked here rather than only in
            // extraction because direct IEntityRepository callers, the TCK bridge and the MCP tools
            // never pass through the extractor, so an extraction-only guard is trivially bypassed.
            IndexKeyBudget.EnsureIndexable(entity.Name, "name", entity.EntityId);
            IndexKeyBudget.EnsureIndexable(entity.CanonicalName, "canonical_name", entity.EntityId);

            var parameters = new Dictionary<string, object?>
            {
                ["id"] = entity.EntityId,
                ["ownerId"] = entity.OwnerId,
                ["name"] = entity.Name,
                ["canonicalName"] = (object?)entity.CanonicalName,
                ["type"] = entity.Type,
                ["subtype"] = (object?)entity.Subtype,
                ["description"] = (object?)entity.Description,
                ["confidence"] = entity.Confidence,
                ["aliases"] = entity.Aliases.ToList(),
                ["attributes"] = SerializeMetadata(entity.Attributes),
                ["sourceMessageIds"] = entity.SourceMessageIds.ToList(),
                ["createdAtUtc"] = entity.CreatedAtUtc.ToString("O"),
                ["metadata"] = SerializeMetadata(entity.Metadata)
            };

            var cursor = await runner.RunAsync(EntityQueries.Upsert, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var node = records.Count > 0 ? records[0]["e"].As<INode>() : null;

            // Persist geospatial location if provided
            if (entity.Latitude.HasValue && entity.Longitude.HasValue)
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityLocation,
                    new { id = entity.EntityId, lat = entity.Latitude.Value, lon = entity.Longitude.Value }).ConfigureAwait(false);
            }

            // Only persist a real (non-empty) vector. A zero-length embedding (the orchestrator's
            // degraded result on a generation failure) must leave the `embedding` property NULL so the
            // back-fill job can later re-process the node — writing `[]` would make `embedding IS NULL`
            // false and strand the node un-searchable forever.
            if (entity.Embedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityEmbedding,
                    new { id = entity.EntityId, embedding = entity.Embedding.ToList() }).ConfigureAwait(false);
            }

            // Dynamically add POLE+O type labels
            var labels = BuildDynamicLabels(entity.Type, entity.Subtype);
            if (labels.Count > 0)
            {
                var labelClause = string.Join(", ", labels.Select(l => $"e:{SanitizeLabel(l)}"));
                await runner.RunAsync($"MATCH (e:Entity {{id: $id}}) SET {labelClause}", new { id = entity.EntityId }).ConfigureAwait(false);
            }

            // Auto-create EXTRACTED_FROM relationships for all source messages
            if (entity.SourceMessageIds.Count > 0)
            {
                await runner.RunAsync(
                    SharedFragments.LinkEntityExtractedFrom,
                    new { id = entity.EntityId, sourceMessageIds = entity.SourceMessageIds.ToList() }).ConfigureAwait(false);
            }

            // The `node` was captured from the MERGE BEFORE the geospatial location was written in a
            // separate query, so MapToEntity finds no `location` property and yields null coords. Carry the
            // caller-supplied (and now-persisted) coordinates onto the returned object so it matches DB state.
            return node is not null
                ? MapToEntity(node, entity.Embedding) with { Latitude = entity.Latitude, Longitude = entity.Longitude }
                : entity;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Entity?> GetByIdAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting entity {Id}", entityId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetById, new { id = entityId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["e"].As<INode>();
            return MapToEntity(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Entity>> GetByNameAsync(
        string name, bool includeAliases = true, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting entities by name '{Name}', includeAliases={IncludeAliases}, owner={Owner}",
            name, includeAliases, scope?.OwnerId);

        var cypher = EntityQueries.GetByName(includeAliases, hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["name"] = name };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) query embedding has no semantic signal and
        // would throw a dimension mismatch at db.index.vector.queryNodes — short-circuit to an empty result.
        if (queryEmbedding is not { Length: > 0 }) return Array.Empty<(Entity, double)>();
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = OwnerVectorOverFetch.InitialTopK(limit, hasOwner);

        // Recall-yield signal, matching Neo4jFactRepository.SearchByVectorAsync. Entities run the same
        // query shape through the same global index, so they are subject to the same post-filter
        // starvation (OwnerVectorOverFetch documents the measurement) — and until this span, nothing
        // reported it. Started AFTER the degraded-embedding short-circuit above, so a search that never
        // reached the index does not publish a zero-yield reading it never earned.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.entity_vector");
        _logger.LogDebug("Vector search entities, limit={Limit}, owner={Owner}", limit, scope?.OwnerId);

        var ranking = _rankingContext?.Current ?? _ranking;   // per-request intent (D3) overrides the configured ranking
        bool recencyRerank = ranking.RecencyRerankEnabled;
        // Payload projection: nothing on the recall path reads the vector back, and it is ~3 KB an item.
        bool omitEmbedding = _omitEmbeddingsFromRecall;
        async Task<List<(Entity, double)>> QueryAsync(int width, CancellationToken ct)
        {
            var cypher = EntityQueries.SearchByVector(
                hasOwner, includeShared, width, recencyRerank, omitEmbedding);
            var parameters = new Dictionary<string, object?>
            {
                ["embedding"] = queryEmbedding.ToList(),
                ["limit"] = limit,
                ["minScore"] = minScore,
            };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            if (recencyRerank) RerankParameters.Add(parameters, ranking, _decay);

            return await _tx.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
                var records = await cursor.ToListAsync().ConfigureAwait(false);
                return records.Select(r =>
                {
                    var score = r["score"].As<double>();
                    // Projected recall returns a MAP, not a Node, and carries no embedding to read.
                    if (omitEmbedding)
                        return (MapToEntity(r["node"].As<IReadOnlyDictionary<string, object>>(), null), score);

                    var node = r["node"].As<INode>();
                    return (MapToEntity(node, ReadEmbedding(node)), score);
                }).ToList();
            }, ct).ConfigureAwait(false) ?? [];
        }

        var results = await QueryAsync(topK, cancellationToken).ConfigureAwait(false);

        // The empty-result rescue, now matching Neo4jFactRepository. MEASURED, not assumed: with 500
        // more-similar foreign entities in the index, an owner-scoped search returned 0 of the owner's
        // 4 entities while the fact path returned 4 of 4 on identical data. The only difference was
        // this retry, so its absence here was a real exposure - an owner could receive NOTHING while
        // its data sat in the graph.
        int? escalatedTopK = null;
        if (OwnerVectorOverFetch.ShouldEscalate(results.Count, hasOwner)
            && await ShouldClimbLadderAsync(scope, includeShared, cancellationToken).ConfigureAwait(false))
        {
            var widened = OwnerVectorOverFetch.EscalatedTopK(topK);
            if (widened > topK)
            {
                _logger.LogDebug(
                    "Owner-scoped entity vector search returned nothing at topK={TopK}; retrying at {Widened}.",
                    topK, widened);
                escalatedTopK = widened;
                results = await QueryAsync(widened, cancellationToken).ConfigureAwait(false);
            }

            // LAST RESORT, removing a ceiling the widening cannot. EscalatedTopK is capped at
            // MaxTopK, so once more than that many foreign rows outrank this owner's, no widening
            // reaches them - measured on the fact path as 4 of 4 at 3,000 competing rows and 0 of 4
            // at 4,000. This scores the owner's OWN rows, so it is bounded by one owner's data rather
            // than the corpus, and runs only when the indexed path and its escalation both returned
            // nothing.
            if (results.Count == 0)
            {
                _logger.LogDebug(
                    "Owner-scoped entity vector search still empty after widening; falling back to a "
                    + "scoped similarity scan.");
                results = await OwnerScopedScanAsync(
                    queryEmbedding, limit, minScore, scope!, includeShared, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else if (_rescueShortOwnerResults
                 && OwnerVectorOverFetch.ShouldRescueShortResult(results.Count, limit, hasOwner))
        {
            // 2.12, the same rescue the fact path carries. Extended here because "a setting only some
            // components respect" is the defect shape this project has found twice -- a host enabling
            // RescueShortOwnerResults would otherwise get it on facts and silently not on entitys.
            _logger.LogDebug(
                "Owner-scoped entity vector search returned {Returned} of {Limit}; rescuing with a "
                + "scoped similarity scan.", results.Count, limit);
            var scanned = await OwnerScopedScanAsync(
                queryEmbedding, limit, minScore, scope!, includeShared, cancellationToken)
                .ConfigureAwait(false);
            if (scanned.Count > results.Count) results = scanned;
        }

        EmitVectorYield(
            activity, hasOwner, limit, escalatedTopK ?? topK, results.Count,
            escalated: escalatedTopK is not null, requestedTopK: topK);
        return results;
    }

    /// <summary>
    /// Publishes what an owner-scoped vector search asked the global index for and what reached the
    /// caller after the post-filter.
    /// </summary>
    /// <param name="activity">The span, or <c>null</c> when nobody is listening.</param>
    /// <param name="hasOwner">Whether an owner post-filter was applied at all.</param>
    /// <param name="limit">The row cap the <i>caller</i> asked for — the denominator of the yield.</param>
    /// <param name="topK">The candidate width actually issued to the index.</param>
    /// <param name="returned">Rows the caller received.</param>
    /// <remarks>
    /// <para>
    /// Every argument is a measured quantity taken from the query that ran; nothing here is derived from
    /// an intention. In particular this deliberately does <b>not</b> report the pre-filter candidate
    /// count — the owner filter and the LIMIT both happen inside Cypher, so that number never crosses
    /// the wire, and publishing a plausible guess for it would be worse than publishing nothing.
    /// </para>
    /// <para>
    /// No <c>escalated</c> tag: unlike the fact search, none of these paths issues a second, wider query
    /// when the first comes back empty. Tagging <c>escalated = false</c> would read as "escalation was
    /// available and declined to fire", which is exactly the distinction a zero-yield reading turns on —
    /// the fact path retries, these give up. Absent, not false.
    /// </para>
    /// </remarks>
    private static void EmitVectorYield(
        Activity? activity, bool hasOwner, int limit, int topK, int returned,
        bool escalated = false, int? requestedTopK = null)
    {
        if (activity is null) return;

        // Explicit null check rather than `activity?.SetTag(...)` per AgentMemoryDiagnostics' remarks:
        // the tag block is the only work here that exists purely to produce telemetry, so it is skipped
        // whole (no boxing, no allocation) when nobody is listening.
        //
        // owner_scoped separates the two populations: unscoped searches have no post-filter and so no
        // starvation to report, and folding them into an aggregate would dilute the very signal this
        // exists to raise. limit is the denominator — `returned` is capped by the Cypher's LIMIT $limit.
        //
        // Called only on the success path: a search that threw is left untagged rather than tagged
        // `returned = 0`, because a failed query measured nothing and a false zero here reads as
        // starvation.
        activity.SetTag("memory.vector.owner_scoped", hasOwner);
        activity.SetTag("memory.vector.limit", limit);
        activity.SetTag("memory.vector.requested_topk", requestedTopK ?? topK);
        // Equal to requested_topk on every entity path, because none of them widens and re-queries. It is
        // emitted all the same so a consumer computing returned / effective_topk gets a correct ratio
        // from any recall span without having to know which sources can escalate and which cannot.
        activity.SetTag("memory.vector.effective_topk", topK);
        // Absent, never defaulted, when no second pass was issued - a width nobody asked for is not a
        // measurement. Matches the fact path exactly, which is the point: entity now escalates too, so
        // reporting escalated=false unconditionally here would have been a fabricated constant.
        if (escalated) activity.SetTag("memory.vector.escalated_topk", topK);
        // Emitted by EVERY vector-recall span, including paths that never escalate. The three
        // conventions this replaces made the telemetry unqueryable: a consumer computing
        // returned/effective_topk had to know which sites emit it, and an omitted "escalated"
        // is indistinguishable from a site that emits no telemetry at all. False here means
        // "no second pass ran", which is exactly what a consumer counting escalations needs.
        activity.SetTag("memory.vector.escalated", escalated);
        activity.SetTag("memory.vector.returned", returned);
    }

    public async Task<IReadOnlyList<Entity>> GetByTypeAsync(string type, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting entities by type {Type}, owner={Owner}", type, scope?.OwnerId);

        var cypher = EntityQueries.GetByType(hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["type"] = type, ["ownerId"] = scope!.OwnerId! }).ConfigureAwait(false)
                : await runner.RunAsync(cypher, new { type }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Entity>> SearchByNameAsync(string name, string? type = null, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Searching entities by name '{Name}', type={Type}, owner={Owner}", name, type, scope?.OwnerId);

        var cypher = EntityQueries.SearchByNameFiltered(type, hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object?> { ["name"] = name, ["type"] = type, ["ownerId"] = scope!.OwnerId }).ConfigureAwait(false)
                : await runner.RunAsync(cypher, new { name, type }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddMentionAsync(string messageId, string entityId, double? confidence = null, int? startPos = null, int? endPos = null, string? context = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding MENTIONS: Message {MessageId} -> Entity {EntityId}", messageId, entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.AddMention, new { messageId, entityId, confidence = (object?)confidence, startPos = (object?)startPos, endPos = (object?)endPos, context = (object?)context }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddMentionsBatchAsync(string messageId, IReadOnlyList<string> entityIds, double? confidence = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding {Count} MENTIONS for Message {MessageId}", entityIds.Count, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.AddMentionsBatch, new { messageId, entityIds = entityIds.ToList(), confidence = (object?)confidence }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddSameAsRelationshipAsync(string entityId1, string entityId2, double confidence, string matchType, string status = "pending", CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding SAME_AS: {EntityId1} <-> {EntityId2} (confidence={Confidence}, matchType={MatchType})",
            entityId1, entityId2, confidence, matchType);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.AddSameAs, new { entityId1, entityId2, confidence, matchType, status }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Confidence, string MatchType)>> GetSameAsEntitiesAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting SAME_AS entities for {EntityId}", entityId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetSameAsEntities, new { entityId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["other"].As<INode>();
                var confidence = r["confidence"].As<double>();
                var matchType = r["matchType"].As<string>();
                return (MapToEntity(node, ReadEmbedding(node)), confidence, matchType);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Entity>> UpsertBatchAsync(IReadOnlyList<Entity> entities, CancellationToken cancellationToken = default)
    {
        if (entities.Count == 0) return Array.Empty<Entity>();

        _logger.LogDebug("Batch upserting {Count} entities", entities.Count);

        var items = entities.Select(e =>
        {
            IndexKeyBudget.EnsureIndexable(e.Name, "name", e.EntityId);
            IndexKeyBudget.EnsureIndexable(e.CanonicalName, "canonical_name", e.EntityId);
            return new Dictionary<string, object?>
        {
            ["id"] = e.EntityId,
            ["owner_id"] = e.OwnerId,
            ["name"] = e.Name,
            ["canonical_name"] = (object?)e.CanonicalName,
            ["type"] = e.Type,
            ["subtype"] = (object?)e.Subtype,
            ["description"] = (object?)e.Description,
            ["confidence"] = e.Confidence,
            ["aliases"] = e.Aliases.ToList(),
            ["attributes"] = SerializeMetadata(e.Attributes),
            ["source_message_ids"] = e.SourceMessageIds.ToList(),
            ["created_at"] = e.CreatedAtUtc.ToString("O"),
            ["metadata"] = SerializeMetadata(e.Metadata)
            };
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.UpsertBatch, new { items }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);

            // Set embeddings individually — only for nodes with a real (non-empty) vector, so a degraded
            // empty embedding leaves `embedding` NULL and re-queueable for the back-fill (see UpsertAsync).
            foreach (var entity in entities.Where(e => e.Embedding is { Length: > 0 }))
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityEmbedding,
                    new { id = entity.EntityId, embedding = entity.Embedding!.ToList() }).ConfigureAwait(false);
            }

            // Persist geospatial location individually (parity with single UpsertAsync — the UNWIND
            // upsert can't set a point() from per-row nullable coords without erroring on missing ones).
            foreach (var entity in entities.Where(e => e.Latitude.HasValue && e.Longitude.HasValue))
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityLocation,
                    new { id = entity.EntityId, lat = entity.Latitude!.Value, lon = entity.Longitude!.Value }).ConfigureAwait(false);
            }

            // Dynamically add POLE+O type labels
            foreach (var entity in entities)
            {
                var labels = BuildDynamicLabels(entity.Type, entity.Subtype);
                if (labels.Count > 0)
                {
                    var labelClause = string.Join(", ", labels.Select(l => $"e:{SanitizeLabel(l)}"));
                    await runner.RunAsync($"MATCH (e:Entity {{id: $id}}) SET {labelClause}", new { id = entity.EntityId }).ConfigureAwait(false);
                }
            }

            // Auto-create EXTRACTED_FROM relationships
            foreach (var entity in entities.Where(e => e.SourceMessageIds.Count > 0))
            {
                await runner.RunAsync(
                    SharedFragments.LinkEntityExtractedFrom,
                    new { id = entity.EntityId, sourceMessageIds = entity.SourceMessageIds.ToList() }).ConfigureAwait(false);
            }

            // Records were read before embeddings/location were written, so carry the caller-supplied
            // embedding AND coordinates onto each returned object so it reflects persisted state (see UpsertAsync).
            var byId = entities.ToDictionary(e => e.EntityId);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                var id = node["id"].As<string>();
                if (!byId.TryGetValue(id, out var src))
                    return MapToEntity(node, null);
                return MapToEntity(node, src.Embedding) with { Latitude = src.Latitude, Longitude = src.Longitude };
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateExtractedFromRelationshipAsync(string entityId, string messageId, double? confidence = null, int? startPos = null, int? endPos = null, string? context = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Entity {EntityId} -> Message {MessageId}", entityId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                EntityQueries.CreateExtractedFrom,
                new { entityId, messageId, confidence = (object?)confidence, startPos = (object?)startPos, endPos = (object?)endPos, context = (object?)context }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> MergeEntitiesAsync(string sourceEntityId, string targetEntityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        // A self-merge (same id for source and target) is meaningless and, run through the merge Cypher, would
        // tombstone the entity (merged_into = itself) and destroy its own now-self-looping relationships — no-op.
        if (string.Equals(sourceEntityId, targetEntityId, StringComparison.Ordinal))
            return false;

        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Merging entity {SourceId} into {TargetId}, owner={Owner}", sourceEntityId, targetEntityId, scope?.OwnerId);

        var cypher = EntityQueries.MergeEntities(hasOwner, includeShared);

        var merged = await _tx.WriteAsync(async runner =>
        {
            var parameters = hasOwner
                ? (object)new Dictionary<string, object> { ["sourceEntityId"] = sourceEntityId, ["targetEntityId"] = targetEntityId, ["ownerId"] = scope!.OwnerId! }
                : new { sourceEntityId, targetEntityId };
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            // The merge Cypher RETURNs source, target — a first row means both endpoints matched (in scope)
            // and the merge ran; no row means a guarded / non-existent no-op.
            return await cursor.FetchAsync().ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        // Only refresh the target's search fields when the merge actually matched (and thus modified) it.
        // A guarded cross-owner / no-match merge returns no rows: skipping the refresh keeps a scoped call
        // from touching (bumping updated_at / rewriting aliases on) another owner's entity — so the guard
        // makes such a call a true no-op, not merely a merge-less one.
        if (merged)
            await RefreshEntitySearchFieldsAsync(targetEntityId, cancellationToken).ConfigureAwait(false);

        // 30.4b. The working-memory block names entities, so a merge that folded one into another can
        // leave it asserting an entity that no longer exists. This is the one rebuild seam with no
        // service to hang an epilogue on -- MergeEntitiesAsync is IEntityRepository-only, so callers
        // reach it directly. It is hooked HERE rather than through a decorator on the interface
        // because this class also implements IUpsertPersistsProvenance, IBatchMemoryRepository<Entity>
        // and IFusedBatchMemoryRepository<Entity>: a wrapper that implements only IEntityRepository
        // silently strips all three, which collapses the batch write paths into per-item queries and
        // re-adds provenance writes the marker exists to skip. That cost 8 -> 115 Cypher queries on
        // the 50-message extraction scenario, caught by the hermetic counter gate.
        if (merged)
            await RebuildWorkingMemoryAfterMergeAsync(targetEntityId, scope, cancellationToken)
                .ConfigureAwait(false);

        return merged;
    }

    /// <summary>
    /// Recompiles the owner's working-memory block after a merge that actually matched. Never throws:
    /// a caller who successfully merged must not see an exception because a projection of it could
    /// not be recompiled.
    /// </summary>
    private async Task RebuildWorkingMemoryAfterMergeAsync(
        string targetEntityId, MemoryScope? scope, CancellationToken cancellationToken)
    {
        var rebuilder = new WorkingMemoryRebuilder(_workingMemory, _workingMemoryOptions, _logger);
        if (rebuilder.IsDisabled) return;

        var ownerId = scope?.OwnerId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            // Unscoped admin/maintenance dedup names no owner, so the surviving target is the only
            // thing that can say whose block changed. Read it AFTER the merge -- the merge folds the
            // source into the target, so the target is the row that survives.
            var target = await GetByIdAsync(targetEntityId, cancellationToken).ConfigureAwait(false);
            ownerId = target?.OwnerId;
        }

        // Guard G3's near side: no owner, no block to rebuild.
        if (string.IsNullOrWhiteSpace(ownerId)) return;

        await rebuilder.RebuildAsync(ownerId, "an entity merge", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RefreshEntitySearchFieldsAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Refreshing search fields for entity {Id}", entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.RefreshSearchFields, new
            {
                entityId,
                updatedAt = DateTimeOffset.UtcNow.ToString("O")
            }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps from a node's properties, so a projected MAP maps identically to a Node.</summary>
    /// <remarks>
    /// The recall payload projection returns <c>node {.*, embedding: NULL}</c>, which is a MAP rather
    /// than a Node. Both paths share this body so the two cannot drift into mapping the same stored
    /// entity differently depending on which query fetched it.
    /// </remarks>
    private static Entity MapToEntity(IReadOnlyDictionary<string, object> properties, float[]? embedding)
    {
        double? latitude = null;
        double? longitude = null;
        if (properties.TryGetValue("location", out var locValue) && locValue is Point pt)
        {
            // WGS-84: X = longitude, Y = latitude
            latitude = pt.Y;
            longitude = pt.X;
        }

        return new Entity
        {
            EntityId = properties["id"].As<string>(),
            OwnerId = properties.TryGetValue("owner_id", out var oid) ? oid.As<string>() : null,
            Name = properties["name"].As<string>(),
            CanonicalName = properties.TryGetValue("canonical_name", out var cn) ? cn.As<string>() : null,
            Type = properties["type"].As<string>(),
            Subtype = properties.TryGetValue("subtype", out var st) ? st.As<string>() : null,
            Description = properties.TryGetValue("description", out var desc) ? desc.As<string>() : null,
            Confidence = properties["confidence"].As<double>(),
            Embedding = embedding,
            Latitude = latitude,
            Longitude = longitude,
            Aliases = properties.TryGetValue("aliases", out var al)
                                ? al.As<IList<object>>().Select(a => a.ToString()!).ToList()
                                : Array.Empty<string>(),
            Attributes = DeserializeMetadata(properties.TryGetValue("attributes", out var attr) ? attr.As<string>() : null),
            SourceMessageIds = properties.TryGetValue("source_message_ids", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc = Neo4jDateTimeHelper.ReadDateTimeOffset(properties["created_at"]),
            UpdatedAtUtc = properties.TryGetValue("updated_at", out var ua) && ua is not null
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(ua)
                                : null,
            Metadata = DeserializeMetadata(properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };
    }

    private static Entity MapToEntity(INode node, float[]? embedding) =>
        MapToEntity(node.Properties, embedding);

    public async Task<Entity?> ApplyConfidenceDeltaAsync(
        string entityId, double delta, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;

        _logger.LogDebug("Applying confidence delta {Delta} to entity {Id}, owner={Owner}", delta, entityId, scope?.OwnerId);

        var cypher = EntityQueries.ApplyConfidenceDelta(hasOwner, includeShared);
        var parameters = new Dictionary<string, object> { ["id"] = entityId, ["delta"] = delta };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId!;

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["e"].As<INode>();
            return MapToEntity(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Entity>> SearchByLocationAsync(
        double latitude,
        double longitude,
        double radiusKm,
        int limit = 10,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Searching entities near ({Lat},{Lon}) radius={RadiusKm}km, owner={Owner}", latitude, longitude, radiusKm, scope?.OwnerId);

        var cypher = EntityQueries.SearchByLocation(hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object>
                {
                    ["lat"] = latitude,
                    ["lon"] = longitude,
                    ["radiusMeters"] = radiusKm * 1000.0,
                    ["limit"] = limit,
                    ["ownerId"] = scope!.OwnerId!,
                }).ConfigureAwait(false)
                : await runner.RunAsync(cypher, new { lat = latitude, lon = longitude, radiusMeters = radiusKm * 1000.0, limit }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Entity>> SearchInBoundingBoxAsync(
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        int limit = 10,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Searching entities in bounding box ({MinLat},{MinLon})-({MaxLat},{MaxLon}), owner={Owner}",
            minLat, minLon, maxLat, maxLon, scope?.OwnerId);

        var cypher = EntityQueries.SearchInBoundingBox(hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object>
                {
                    ["minLat"] = minLat,
                    ["minLon"] = minLon,
                    ["maxLat"] = maxLat,
                    ["maxLon"] = maxLon,
                    ["limit"] = limit,
                    ["ownerId"] = scope!.OwnerId!,
                }).ConfigureAwait(false)
                : await runner.RunAsync(cypher, new { minLat, minLon, maxLat, maxLon, limit }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<Entity>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting up to {Limit} entities without embeddings", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetPageWithoutEmbedding, new { limit = limit + 1 }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var items = records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, null);
            }).ToList();
            return PaginationHelper.ApplyPagination(items, limit);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateEmbeddingAsync(
        string entityId,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        // Never overwrite with a zero-length vector (e.g. a back-fill run that itself hit a transient
        // embedding failure). Skipping keeps `embedding` NULL so the node stays re-queueable rather than
        // being poisoned with `[]` and stranded un-searchable.
        if (embedding.Length == 0)
        {
            _logger.LogDebug("Skipping empty embedding update for entity {Id}.", entityId);
            return;
        }

        _logger.LogDebug("Updating embedding for entity {Id}", entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                EntityQueries.UpdateEmbedding,
                new { id = entityId, embedding = embedding.ToList() }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Deleting entity {Id}, owner={Owner}", entityId, scope?.OwnerId);

        var cypher = EntityQueries.Delete(hasOwner);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["entityId"] = entityId, ["ownerId"] = scope!.OwnerId! }).ConfigureAwait(false)
                : await runner.RunAsync(cypher, new { entityId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["deleted"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> InvalidateAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Invalidating entity {Id}, owner={Owner}", entityId, scope?.OwnerId);

        var cypher = EntityQueries.Invalidate(hasOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["id"] = entityId, ["now"] = now };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["invalidated"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Similarity)>> FindSimilarByEmbeddingAsync(
        string entityId, double minSimilarity = 0.85, int limit = 10, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        // Over-fetch when scoped so a high-volume foreign owner can't starve the post-filter result set.
        // The +1 is the SELF slot: this probes the index with the source entity's own embedding, so the
        // source is its own nearest neighbour and is guaranteed to occupy one candidate before
        // `WHERE node.id <> $entityId` drops it. It widens the query, not the caller's ask — the Cypher
        // still ends `LIMIT $limit`, so `limit` remains what the caller asked for and stays the correct
        // denominator for the yield below.
        int topK = OwnerVectorOverFetch.InitialTopK(limit + 1, hasOwner);

        // Same global index and same owner post-filter as SearchByVectorAsync, so the same starvation
        // applies: this is the "find duplicates of my entity" surface, and a duplicate crowded out by
        // foreign candidates is silently never merged.
        //
        // One reading here is genuinely ambiguous and is left that way on purpose. The query opens
        // `MATCH (source:Entity {id: $entityId}) WHERE source.embedding IS NOT NULL`, so a missing or
        // un-embedded source yields zero rows without the index ever being probed — indistinguishable,
        // from out here, from a real starvation. Separating the two needs either a second query or a
        // wider projection; both cost more than the ambiguity, and inventing a distinction we did not
        // measure would be worse than admitting it.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.entity_similar_vector");
        _logger.LogDebug("Finding similar entities for {EntityId}, minSimilarity={MinSimilarity}, limit={Limit}, owner={Owner}",
            entityId, minSimilarity, limit, scope?.OwnerId);

        var cypher = EntityQueries.FindSimilarByEmbedding(hasOwner, includeShared);

        var results = await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["entityId"] = entityId, ["topK"] = topK, ["minSimilarity"] = minSimilarity, ["limit"] = limit, ["ownerId"] = scope!.OwnerId! }).ConfigureAwait(false)
                : await runner.RunAsync(cypher, new { entityId, topK, minSimilarity, limit }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToEntity(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        // limit, not limit + 1: the internal self slot is a property of the query, not of what the caller
        // requested, and reporting 11 would understate every yield ratio this signal exists to expose.
        EmitVectorYield(activity, hasOwner, limit, topK, results.Count);
        return results;
    }

    public async Task<IReadOnlyList<DuplicatePair>> GetPendingDuplicatesAsync(
        int limit = 50, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting pending duplicate pairs, limit={Limit}", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetPendingDuplicates, new { limit }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var source = MapToEntity(r["a"].As<INode>(), ReadEmbedding(r["a"].As<INode>()));
                var target = MapToEntity(r["b"].As<INode>(), ReadEmbedding(r["b"].As<INode>()));
                var similarity = r["similarity"].As<double>();
                // This API returns only pending pairs — GetPendingDuplicates hard-filters status: 'pending'.
                return new DuplicatePair(source, target, similarity, DuplicateStatus.Pending);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeduplicationStats> GetDeduplicationStatsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting deduplication stats");

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetDeduplicationStats, new { }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0)
                return new DeduplicationStats(0, 0, 0, 0);

            var record = records[0];
            return new DeduplicationStats(
                PendingCount: record["pending"].As<int>(),
                ConfirmedCount: record["confirmed"].As<int>(),
                RejectedCount: record["rejected"].As<int>(),
                MergedCount: record["merged"].As<int>());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Entity>> GetEntitiesFromMessageAsync(
        string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting entities from message {MessageId}", messageId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetEntitiesFromMessage, new { messageId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) query embedding short-circuits to empty.
        if (queryEmbedding is not { Length: > 0 }) return Array.Empty<(Entity, double)>();
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = OwnerVectorOverFetch.InitialTopK(limit, hasOwner);

        // Its own span, not the live one: a point-in-time search discards everything created after the
        // cutoff on top of the owner post-filter, so folding its yield in with live recall would blame
        // the owner filter for a temporal exclusion.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.entity_vector_as_of");
        _logger.LogDebug("Temporal vector search entities as of {AsOf}, limit={Limit}, owner={Owner}", asOf, limit, scope?.OwnerId);

        var cypher = TemporalQueries.SearchEntitiesAsOf(hasOwner, includeShared, topK);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["limit"] = limit,
            ["minScore"] = minScore,
            // D6: entities have only the transaction clock, so the AsOf timestamp binds $systemAsOf.
            ["systemAsOf"] = asOf.UtcDateTime.ToString("O")
        };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        var results = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToEntity(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        EmitVectorYield(activity, hasOwner, limit, topK, results.Count);
        return results;
    }

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    private static readonly HashSet<string> ValidEntityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "PERSON", "OBJECT", "LOCATION", "EVENT", "ORGANIZATION",
        "INDIVIDUAL", "GROUP", "ANIMAL", "VEHICLE", "BUILDING", "LANDMARK",
        "CITY", "COUNTRY", "REGION", "ADDRESS", "COMPANY", "GOVERNMENT",
        "CONFERENCE", "MEETING", "INCIDENT"
    };

    internal static List<string> BuildDynamicLabels(string type, string? subtype)
    {
        var labels = new List<string>();
        var sanitizedType = SanitizeLabel(type);
        if (!string.IsNullOrEmpty(sanitizedType) && ValidEntityLabels.Contains(sanitizedType))
            labels.Add(sanitizedType.ToUpperInvariant());
        if (!string.IsNullOrEmpty(subtype))
        {
            var sanitizedSubtype = SanitizeLabel(subtype);
            if (!string.IsNullOrEmpty(sanitizedSubtype) && ValidEntityLabels.Contains(sanitizedSubtype))
                labels.Add(sanitizedSubtype.ToUpperInvariant());
        }
        return labels;
    }

    internal static string SanitizeLabel(string label)
    {
        return new string(label.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

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
                OwnerRowExistence.Any("Entity", includeShared),
                new { ownerId = scope.OwnerId }).ConfigureAwait(false);
            return (await cursor.ToListAsync().ConfigureAwait(false)).Count > 0;
        }, cancellationToken).ConfigureAwait(false);

        if (!present)
        {
            _logger.LogDebug(
                "Owner {Owner} holds no Entity rows; skipping the escalation ladder (2.13).",
                scope.OwnerId);
        }

        return present;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Entity>> ListCreatedInWindowAsync(
        DateTimeOffset since, DateTimeOffset until, MemoryScope? scope, int maxPerBucket,
        CancellationToken cancellationToken = default)
    {
        var hasOwner = scope?.OwnerId is not null;
        var parameters = new Dictionary<string, object?>
        {
            ["since"] = since.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["until"] = until.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = maxPerBucket,
        };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                EntityQueries.DeltaNewEntities(hasOwner, scope?.IncludeShared ?? false), parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyList<Entity>)records
                .Select(r => MapToEntity(r["e"].As<INode>(), null)).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? Array.Empty<Entity>();
    }}