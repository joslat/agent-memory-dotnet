using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jPreferenceRepository : IPreferenceRepository, IUpsertPersistsProvenance,
    IBatchMemoryRepository<Preference>, IFusedBatchMemoryRepository<Preference>
{

    private readonly INeo4jTransactionRunner _tx;
    private readonly bool _rescueShortOwnerResults;
    /// <summary>2.13: skip a futile widened probe + scan for an owner holding nothing.</summary>
    private readonly bool _skipEscalationWhenOwnerHasNoRows;
    /// <summary>Payload projection: drop the ~3 KB vector nothing on the recall path reads.</summary>
    private readonly bool _omitEmbeddingsFromRecall;
    private readonly ILogger<Neo4jPreferenceRepository> _logger;
    private readonly MemoryRankingOptions _ranking;
    private readonly MemoryDecayOptions _decay;
    /// <summary>Scores this owner's OWN preferences directly, bypassing the global vector index.</summary>
    /// <remarks>
    /// Extracted because two conditions reach it -- an empty scoped result, and (opt-in) a short one
    /// -- and two copies of a fallback drift.
    /// </remarks>
    private async Task<List<(Preference, double)>> OwnerScopedScanAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope scope,
        bool includeShared,
        CancellationToken cancellationToken) =>
        await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                PreferenceQueries.SearchByVectorOwnerScopedFallback(includeShared),
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
                return (MapToPreference(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

    private readonly IMemoryRankingContext? _rankingContext;

    public Neo4jPreferenceRepository(
        INeo4jTransactionRunner tx,
        ILogger<Neo4jPreferenceRepository> logger,
        IOptions<MemoryRankingOptions>? ranking = null,
        IOptions<MemoryDecayOptions>? decay = null,
        IMemoryRankingContext? rankingContext = null,
        IOptions<MemoryOptions>? memoryOptions = null)
    {
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

    public async Task<Preference> UpsertAsync(Preference preference, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting preference {Id}", preference.PreferenceId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"] = preference.PreferenceId,
                ["ownerId"] = preference.OwnerId,
                ["category"] = preference.Category,
                ["preferenceText"] = preference.PreferenceText,
                ["context"] = (object?)preference.Context,
                ["confidence"] = preference.Confidence,
                ["sourceMessageIds"] = preference.SourceMessageIds.ToList(),
                ["createdAtUtc"] = preference.CreatedAtUtc.ToString("O"),
                ["metadata"] = SerializeMetadata(preference.Metadata)
            };

            var cursor = await runner.RunAsync(PreferenceQueries.Upsert, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            var node = record["p"].As<INode>();

            // Only persist a real (non-empty) vector so a degraded empty embedding leaves `embedding`
            // NULL and re-queueable for the back-fill (mirrors the entity/fact repos).
            if (preference.Embedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    PreferenceQueries.SetEmbedding,
                    new { id = preference.PreferenceId, embedding = preference.Embedding.ToList() }).ConfigureAwait(false);
            }

            // Auto-create EXTRACTED_FROM relationships for all source messages
            if (preference.SourceMessageIds.Count > 0)
            {
                await runner.RunAsync(
                    PreferenceQueries.CreateExtractedFromMessages,
                    new { id = preference.PreferenceId, sourceMessageIds = preference.SourceMessageIds.ToList() }).ConfigureAwait(false);
            }

            return MapToPreference(node, preference.Embedding);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Preference>> UpsertBatchAsync(
        IReadOnlyList<Preference> preferences,
        CancellationToken cancellationToken = default)
    {
        if (preferences.Count == 0) return Array.Empty<Preference>();

        _logger.LogDebug("Batch upserting {Count} preferences", preferences.Count);
        var items = preferences.Select(preference => new Dictionary<string, object?>
        {
            ["id"] = preference.PreferenceId,
            ["owner_id"] = preference.OwnerId,
            ["category"] = preference.Category,
            ["preference"] = preference.PreferenceText,
            ["context"] = preference.Context,
            ["confidence"] = preference.Confidence,
            ["source_message_ids"] = preference.SourceMessageIds.ToList(),
            ["created_at"] = preference.CreatedAtUtc.ToString("O"),
            ["metadata"] = SerializeMetadata(preference.Metadata)
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PreferenceQueries.UpsertBatch, new { items }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);

            foreach (var preference in preferences.Where(item => item.Embedding is { Length: > 0 }))
            {
                await runner.RunAsync(
                    PreferenceQueries.SetEmbedding,
                    new { id = preference.PreferenceId, embedding = preference.Embedding!.ToList() }).ConfigureAwait(false);
            }

            foreach (var preference in preferences.Where(item => item.SourceMessageIds.Count > 0))
            {
                await runner.RunAsync(
                    PreferenceQueries.CreateExtractedFromMessages,
                    new { id = preference.PreferenceId, sourceMessageIds = preference.SourceMessageIds.ToList() })
                    .ConfigureAwait(false);
            }

            var byId = preferences.ToDictionary(item => item.PreferenceId, StringComparer.Ordinal);
            return records.Select(record =>
            {
                var node = record["p"].As<INode>();
                var id = node["id"].As<string>();
                return MapToPreference(node, byId.TryGetValue(id, out var source) ? source.Embedding : null);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
    public async Task<Preference?> GetByIdAsync(string preferenceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting preference {Id}", preferenceId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PreferenceQueries.GetById, new { id = preferenceId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["p"].As<INode>();
            return MapToPreference(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Preference>> GetByCategoryAsync(
        string category, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting preferences by category '{Category}', owner={Owner}", category, scope?.OwnerId);

        var cypher = PreferenceQueries.GetByCategory(hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["category"] = category };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["p"].As<INode>();
                return MapToPreference(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) query embedding has no semantic signal and
        // would throw a dimension mismatch at db.index.vector.queryNodes — short-circuit to an empty result.
        if (queryEmbedding is not { Length: > 0 }) return Array.Empty<(Preference, double)>();
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = OwnerVectorOverFetch.InitialTopK(limit, hasOwner);

        // Recall-yield signal, mirroring Neo4jFactRepository.SearchByVectorAsync. The vector index is
        // global, so the owner filter is a POST-filter on a top-K drawn from every tenant (see
        // OwnerVectorOverFetch): how much of that budget actually reached the querying owner was
        // measurable on the fact path only, and invisible here. Started AFTER the degraded-embedding
        // short-circuit above, so a search that never reached the index does not publish a zero-yield
        // reading it never earned.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.preference_vector");
        _logger.LogDebug("Vector search preferences, limit={Limit}, owner={Owner}", limit, scope?.OwnerId);

        var ranking = _rankingContext?.Current ?? _ranking;   // per-request intent (D3) overrides the configured ranking
        bool recencyRerank = ranking.RecencyRerankEnabled;
        async Task<List<(Preference, double)>> QueryAsync(int width, CancellationToken ct)
        {
            var cypher = PreferenceQueries.SearchByVector(
                hasOwner, includeShared, width, recencyRerank, _omitEmbeddingsFromRecall);
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
                    if (_omitEmbeddingsFromRecall)
                        return (MapToPreference(r["node"].As<IReadOnlyDictionary<string, object>>(), null), score);

                    var node = r["node"].As<INode>();
                    return (MapToPreference(node, ReadEmbedding(node)), score);
                }).ToList();
            }, ct).ConfigureAwait(false) ?? [];
        }

        var results = await QueryAsync(topK, cancellationToken).ConfigureAwait(false);

        // The empty-result rescue, matching the fact and entity paths. Measured on the entity path
        // with the same query shape: 500 more-similar foreign rows drove an owner-scoped search to
        // 0 of the owner's 4 rows, and this retry restored all 4. Preferences run the identical
        // global-index-then-post-filter shape, so the exposure was identical.
        int? escalatedTopK = null;
        if (OwnerVectorOverFetch.ShouldEscalate(results.Count, hasOwner)
            && await ShouldClimbLadderAsync(scope, includeShared, cancellationToken).ConfigureAwait(false))
        {
            var widened = OwnerVectorOverFetch.EscalatedTopK(topK);
            if (widened > topK)
            {
                _logger.LogDebug(
                    "Owner-scoped preference vector search returned nothing at topK={TopK}; retrying at {Widened}.",
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
                    "Owner-scoped preference vector search still empty after widening; falling back to a "
                    + "scoped similarity scan.");
                results = await OwnerScopedScanAsync(
                    queryEmbedding, limit, minScore, scope!, includeShared, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else if (_rescueShortOwnerResults
                 && OwnerVectorOverFetch.ShouldRescueShortResult(results.Count, limit, hasOwner))
        {
            // 2.12, the same rescue the fact path carries. A host enabling RescueShortOwnerResults
            // would otherwise get it on facts and silently not on preferences -- the "setting only some
            // components respect" shape this project has found twice.
            _logger.LogDebug(
                "Owner-scoped preference vector search returned {Returned} of {Limit}; rescuing with "
                + "a scoped similarity scan.", results.Count, limit);
            var scanned = await OwnerScopedScanAsync(
                queryEmbedding, limit, minScore, scope!, includeShared, cancellationToken)
                .ConfigureAwait(false);
            if (scanned.Count > results.Count) results = scanned;
        }

        // Explicit null check rather than `activity?.SetTag(...)` per AgentMemoryDiagnostics' remarks: the
        // tag block exists purely to produce telemetry, so it is skipped whole when nobody is listening.
        // Success path only — a search that threw measured nothing, and tagging it `returned = 0` would be
        // indistinguishable from a genuine total-starvation reading.
        if (activity is not null)
        {
            TagVectorYield(
                activity, hasOwner, limit, escalatedTopK ?? topK, results.Count,
                escalated: escalatedTopK is not null, requestedTopK: topK);
        }

        return results;
    }

    /// <summary>
    /// The recall-yield tags shared by both preference vector paths.
    /// </summary>
    /// <remarks>
    /// <c>owner_scoped</c> separates the two populations: an unscoped search has no post-filter and so no
    /// starvation to report, and folding them together would dilute the signal. <c>limit</c> is the other
    /// half of the denominator — <c>returned</c> is capped by the Cypher's <c>LIMIT $limit</c>, so 7 rows
    /// means something different at limit 10 than at limit 7.
    /// <para>
    /// <c>effective_topk</c> is the width that actually produced <c>returned</c>. On these paths it always
    /// equals <c>requested_topk</c> because exactly one query is issued, but it is emitted anyway so a
    /// consumer computing <c>returned / effective_topk</c> across every vector span gets a correct ratio
    /// without having to know which sources can widen and which cannot.
    /// </para>
    /// <para>
    /// <c>escalated</c> is emitted as <c>false</c>, not omitted. Neither preference search retries an
    /// empty scoped result at a wider topK, and an earlier version of this remark argued that emitting
    /// <c>false</c> would dilute any measure of how often the rescue fires. That reasoning was
    /// overturned when the same argument, applied site by site, produced <b>three different tag
    /// vocabularies</b> across eight searches: an omitted tag is indistinguishable from a site that
    /// emits no telemetry at all, so a consumer could not read them with one query. <c>false</c> here
    /// means "no second pass ran" — an escalation count filters on <c>escalated = true</c> and is
    /// unaffected.
    /// </para>
    /// <para>
    /// Still absent, and this part stands: <c>escalated_topk</c> when no second query ran — a width
    /// nobody asked for is not a measurement — and the true pre-filter candidate count, since the
    /// owner filter and LIMIT both run inside Cypher, so that number never reaches this process and a
    /// plausible guess would be worse than silence.
    /// </para>
    /// </remarks>
    private static void TagVectorYield(
        Activity activity, bool hasOwner, int limit, int topK, int returned,
        bool escalated = false, int? requestedTopK = null)
    {
        activity.SetTag("memory.vector.owner_scoped", hasOwner);
        activity.SetTag("memory.vector.limit", limit);
        activity.SetTag("memory.vector.requested_topk", requestedTopK ?? topK);
        activity.SetTag("memory.vector.effective_topk", topK);
        activity.SetTag("memory.vector.escalated", escalated);
        // Absent, never defaulted, when no second pass ran - a width nobody asked for is not a
        // measurement. The live path can now escalate, so a hardcoded false would be fabricated.
        if (escalated) activity.SetTag("memory.vector.escalated_topk", topK);
        activity.SetTag("memory.vector.returned", returned);
    }

    private const int DedupOverFetch = 10;

    public async Task<Preference?> FindDuplicateAsync(
        string category, float[] embedding, string? ownerId, double threshold,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) embedding can't address the vector index;
        // there is no duplicate to find, so short-circuit (caller then creates a new node).
        if (embedding is not { Length: > 0 }) return null;
        bool ownerIsShared = string.IsNullOrEmpty(ownerId);
        var cypher = PreferenceQueries.FindDuplicate(DedupOverFetch, ownerIsShared);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = embedding.ToList(),
            ["threshold"] = threshold,
            ["category"] = category,
        };
        if (!ownerIsShared) parameters["ownerId"] = ownerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["node"].As<INode>();
            return MapToPreference(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Preference?> MarkDeduplicatedAsync(string preferenceId, double confidence, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Reinforcing preference {Id} via dedup (confidence={Confidence}).", preferenceId, confidence);
        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PreferenceQueries.MarkDeduplicated, new { id = preferenceId, confidence }).ConfigureAwait(false);
            // 0/1-row read (not SingleAsync): the node can be concurrently hard-deleted between the dedup
            // lookup and this write (e.g. a destructive decay prune), leaving an empty result.
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["p"].As<INode>();
            return MapToPreference(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string preferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Deleting preference {Id}, owner={Owner}", preferenceId, scope?.OwnerId);

        var cypher = PreferenceQueries.Delete(hasOwner);

        await _tx.WriteAsync(async runner =>
        {
            if (hasOwner)
                await runner.RunAsync(cypher, new Dictionary<string, object> { ["id"] = preferenceId, ["ownerId"] = scope!.OwnerId! }).ConfigureAwait(false);
            else
                await runner.RunAsync(cypher, new { id = preferenceId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> InvalidateAsync(string preferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Invalidating preference {Id}, owner={Owner}", preferenceId, scope?.OwnerId);

        var cypher = PreferenceQueries.Invalidate(hasOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["id"] = preferenceId, ["now"] = now };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["invalidated"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SupersedeAsync(string loserPreferenceId, string winnerPreferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Superseding preference {Loser} with {Winner}, owner={Owner}", loserPreferenceId, winnerPreferenceId, scope?.OwnerId);

        var cypher = PreferenceQueries.Supersede(hasOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["loserId"] = loserPreferenceId, ["winnerId"] = winnerPreferenceId, ["now"] = now };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["superseded"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateExtractedFromRelationshipAsync(string preferenceId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Preference {PreferenceId} -> Message {MessageId}", preferenceId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.CreateExtractedFromRelationship,
                new { preferenceId, messageId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAboutRelationshipAsync(string preferenceId, string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating ABOUT: Preference {PreferenceId} -> Entity {EntityId}", preferenceId, entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.CreateAboutRelationship,
                new { preferenceId, entityId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateConversationPreferenceRelationshipAsync(string conversationId, string preferenceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_PREFERENCE: Conversation {ConversationId} -> Preference {PreferenceId}", conversationId, preferenceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.CreateConversationPreferenceRelationship,
                new { conversationId, preferenceId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps from properties, so a projected MAP maps identically to a Node.</summary>
    /// <remarks>
    /// Recall payload projection returns <c>node {.*, embedding: NULL}</c> — a MAP, not a Node.
    /// Both paths share this body so the same stored row cannot map differently depending on
    /// which query fetched it.
    /// </remarks>
    private static Preference MapToPreference(IReadOnlyDictionary<string, object> properties, float[]? embedding) =>
        new()
        {
            PreferenceId = properties["id"].As<string>(),
            OwnerId = properties.TryGetValue("owner_id", out var oid) ? oid.As<string>() : null,
            Category = properties["category"].As<string>(),
            PreferenceText = properties["preference"].As<string>(),
            Context = properties.TryGetValue("context", out var ctx) ? ctx.As<string>() : null,
            Confidence = properties["confidence"].As<double>(),
            Embedding = embedding,
            SourceMessageIds = properties.TryGetValue("source_message_ids", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc = Neo4jDateTimeHelper.ReadDateTimeOffset(properties["created_at"]),
            Metadata = DeserializeMetadata(properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static Preference MapToPreference(INode node, float[]? embedding) =>
        MapToPreference(node.Properties, embedding);

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    public async Task<PagedResult<Preference>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting up to {Limit} preferences without embeddings", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PreferenceQueries.GetPageWithoutEmbedding, new { limit = limit + 1 }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var items = records.Select(r =>
            {
                var node = r["p"].As<INode>();
                return MapToPreference(node, null);
            }).ToList();
            return PaginationHelper.ApplyPagination(items, limit);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateEmbeddingAsync(
        string preferenceId,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        // Never overwrite with a zero-length vector — keep `embedding` NULL so the node stays
        // re-queueable for the back-fill rather than being poisoned with `[]`.
        if (embedding.Length == 0)
        {
            _logger.LogDebug("Skipping empty embedding update for preference {Id}.", preferenceId);
            return;
        }

        _logger.LogDebug("Updating embedding for preference {Id}", preferenceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.UpdateEmbedding,
                new { id = preferenceId, embedding = embedding.ToList() }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) query embedding short-circuits to empty.
        if (queryEmbedding is not { Length: > 0 }) return Array.Empty<(Preference, double)>();
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = OwnerVectorOverFetch.InitialTopK(limit, hasOwner);

        // Same yield signal as the live path, under its own span name: a point-in-time recall and a live
        // one answer different questions, and folding them into one name would make either unreadable.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.preference_vector_as_of");
        _logger.LogDebug("Temporal vector search preferences as of {AsOf}, limit={Limit}, owner={Owner}", asOf, limit, scope?.OwnerId);

        var cypher = TemporalQueries.SearchPreferencesAsOf(hasOwner, includeShared, topK);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["limit"] = limit,
            ["minScore"] = minScore,
            // D6: preferences have only the transaction clock, so the AsOf timestamp binds $systemAsOf.
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
                return (MapToPreference(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        if (activity is not null) TagVectorYield(activity, hasOwner, limit, topK, results.Count);

        return results;
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
                OwnerRowExistence.Any("Preference", includeShared),
                new { ownerId = scope.OwnerId }).ConfigureAwait(false);
            return (await cursor.ToListAsync().ConfigureAwait(false)).Count > 0;
        }, cancellationToken).ConfigureAwait(false);

        if (!present)
        {
            _logger.LogDebug(
                "Owner {Owner} holds no Preference rows; skipping the escalation ladder (2.13).",
                scope.OwnerId);
        }

        return present;
    }

    /// <inheritdoc/>
    public async Task<PreferenceDeltaRows> ListChangedInWindowAsync(
        DateTimeOffset since, DateTimeOffset until, MemoryScope? scope, int maxPerBucket,
        CancellationToken cancellationToken = default)
    {
        var hasOwner = scope?.OwnerId is not null;
        var includeShared = scope?.IncludeShared ?? false;
        var parameters = new Dictionary<string, object?>
        {
            ["since"] = since.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["until"] = until.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = maxPerBucket,
        };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var newCursor = await runner.RunAsync(
                PreferenceQueries.DeltaNewPreferences(hasOwner, includeShared), parameters).ConfigureAwait(false);
            var newRecords = await newCursor.ToListAsync().ConfigureAwait(false);

            var pairCursor = await runner.RunAsync(
                PreferenceQueries.DeltaSupersededPreferences(hasOwner, includeShared), parameters).ConfigureAwait(false);
            var pairRecords = await pairCursor.ToListAsync().ConfigureAwait(false);

            return new PreferenceDeltaRows
            {
                NewPreferences = newRecords.Select(r => MapToPreference(r["p"].As<INode>(), null)).ToList(),
                SupersededPreferences = pairRecords
                    .Select(r => new SupersededPreferencePair(
                        MapToPreference(r["old"].As<INode>(), null),
                        MapToPreference(r["new"].As<INode>(), null)))
                    .ToList(),
            };
        }, cancellationToken).ConfigureAwait(false) ?? new PreferenceDeltaRows();
    }}