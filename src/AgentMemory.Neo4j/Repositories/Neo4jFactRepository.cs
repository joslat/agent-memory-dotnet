using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

public sealed class Neo4jFactRepository : IFactRepository
{
    // Owner-scoped vector search over-fetches candidates (topK > limit) so an owner filter is not
    // starved by higher-scoring foreign rows; the post-WHERE then LIMITs to the requested count (R1).
    internal const int OwnerOverFetchFactor = 5;
    internal const int OwnerOverFetchFloor = 50;

    // Non-null sentinel for the shared/global owner, used only as the MERGE-pattern owner_key so that
    // a shared fact (owner_id null) stays distinct from owned facts with the same S/P/O triple.
    internal const string OwnerKeyShared = "*";

    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jFactRepository> _logger;
    private readonly MemoryRankingOptions _ranking;
    private readonly MemoryDecayOptions _decay;
    private readonly IMemoryRankingContext? _rankingContext;

    public Neo4jFactRepository(
        INeo4jTransactionRunner tx,
        ILogger<Neo4jFactRepository> logger,
        IOptions<MemoryRankingOptions>? ranking = null,
        IOptions<MemoryDecayOptions>? decay = null,
        IMemoryRankingContext? rankingContext = null)
    {
        _tx = tx;
        _logger = logger;
        _ranking = ranking?.Value ?? MemoryRankingOptions.Default;
        _decay = decay?.Value ?? MemoryDecayOptions.Default;
        _rankingContext = rankingContext;
    }

    public async Task<Fact> UpsertAsync(Fact fact, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting fact {Id}", fact.FactId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"]               = fact.FactId,
                ["subject"]          = fact.Subject,
                ["predicate"]        = fact.Predicate,
                ["object"]           = fact.Object,
                ["ownerId"]          = fact.OwnerId,
                ["ownerKey"]         = fact.OwnerId ?? OwnerKeyShared,
                ["category"]         = fact.Category,
                ["confidence"]       = fact.Confidence,
                ["validFrom"]        = (object?)(fact.ValidFrom?.ToString("O")),
                ["validUntil"]       = (object?)(fact.ValidUntil?.ToString("O")),
                ["sourceMessageIds"] = fact.SourceMessageIds.ToList(),
                ["createdAtUtc"]     = fact.CreatedAtUtc.ToString("O"),
                ["updatedAtUtc"]     = DateTimeOffset.UtcNow.ToString("O"),
                ["metadata"]         = SerializeMetadata(fact.Metadata)
            };

            var cursor = await runner.RunAsync(FactQueries.Upsert, parameters);
            var record = await cursor.SingleAsync();
            var node = record["f"].As<INode>();

            // Only persist a real (non-empty) vector so a degraded empty embedding leaves `embedding`
            // NULL and re-queueable for the back-fill (mirrors the entity/preference repos).
            if (fact.Embedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    SharedFragments.SetFactEmbedding,
                    new { id = fact.FactId, embedding = fact.Embedding.ToList() });
            }

            // Auto-create EXTRACTED_FROM relationships for all source messages
            if (fact.SourceMessageIds.Count > 0)
            {
                await runner.RunAsync(
                    SharedFragments.LinkFactExtractedFrom,
                    new { id = fact.FactId, sourceMessageIds = fact.SourceMessageIds.ToList() });
            }

            return MapToFact(node, fact.Embedding);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Fact>> UpsertBatchAsync(IReadOnlyList<Fact> facts, CancellationToken cancellationToken = default)
    {
        if (facts.Count == 0) return Array.Empty<Fact>();

        _logger.LogDebug("Batch upserting {Count} facts", facts.Count);

        var items = facts.Select(f => new Dictionary<string, object?>
        {
            ["id"]                = f.FactId,
            ["subject"]           = f.Subject,
            ["predicate"]         = f.Predicate,
            ["object"]            = f.Object,
            ["owner_id"]          = f.OwnerId,
            ["owner_key"]         = f.OwnerId ?? OwnerKeyShared,
            ["category"]          = f.Category,
            ["confidence"]        = f.Confidence,
            ["valid_from"]        = (object?)(f.ValidFrom?.ToString("O")),
            ["valid_until"]       = (object?)(f.ValidUntil?.ToString("O")),
            ["source_message_ids"] = f.SourceMessageIds.ToList(),
            ["created_at"]        = f.CreatedAtUtc.ToString("O"),
            ["metadata"]          = SerializeMetadata(f.Metadata)
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.UpsertBatch, new { items });
            var records = await cursor.ToListAsync();

            // Set embeddings individually — only for nodes with a real (non-empty) vector.
            foreach (var fact in facts.Where(f => f.Embedding is { Length: > 0 }))
            {
                await runner.RunAsync(
                    SharedFragments.SetFactEmbedding,
                    new { id = fact.FactId, embedding = fact.Embedding!.ToList() });
            }

            // Auto-create EXTRACTED_FROM relationships
            var factsWithSources = facts.Where(f => f.SourceMessageIds.Count > 0).ToList();
            if (factsWithSources.Count > 0)
            {
                foreach (var fact in factsWithSources)
                {
                    await runner.RunAsync(
                        SharedFragments.LinkFactExtractedFrom,
                        new { id = fact.FactId, sourceMessageIds = fact.SourceMessageIds.ToList() });
                }
            }

            var embeddingMap = facts.ToDictionary(f => f.FactId, f => f.Embedding);
            return records.Select(r =>
            {
                var node = r["f"].As<INode>();
                var id   = node["id"].As<string>();
                return MapToFact(node, embeddingMap.TryGetValue(id, out var emb) ? emb : null);
            }).ToList();
        }, cancellationToken);
    }

    public async Task<Fact?> GetByIdAsync(string factId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting fact {Id}", factId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.GetById, new { id = factId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["f"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Fact>> GetBySubjectAsync(
        string subject, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting facts by subject '{Subject}', owner={Owner}", subject, scope?.OwnerId);

        var cypher = FactQueries.GetBySubject(hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["subject"] = subject };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["f"].As<INode>();
                return MapToFact(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = hasOwner ? Math.Max(limit * OwnerOverFetchFactor, limit + OwnerOverFetchFloor) : limit;
        _logger.LogDebug("Vector search facts, limit={Limit}, owner={Owner}", limit, scope?.OwnerId);

        var ranking = _rankingContext?.Current ?? _ranking;   // per-request intent (D3) overrides the configured ranking
        bool recencyRerank = ranking.RecencyRerankEnabled;
        var cypher = FactQueries.SearchByVector(hasOwner, includeShared, topK, recencyRerank);
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
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToFact(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    // Small candidate set for dedup lookups: a near-duplicate by subject+predicate is rare, so a
    // modest over-fetch is enough to find the best match above the (high) similarity threshold.
    private const int DedupOverFetch = 10;

    public async Task<Fact?> FindDuplicateAsync(
        string subject, string predicate, float[] embedding, string? ownerId, double threshold,
        CancellationToken cancellationToken = default)
    {
        var cypher = FactQueries.FindDuplicate(DedupOverFetch);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"]  = embedding.ToList(),
            ["threshold"]  = threshold,
            ["subject"]    = subject,
            ["predicate"]  = predicate,
            ["ownerKey"]   = ownerId ?? OwnerKeyShared,
        };

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["node"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<Fact> MarkDeduplicatedAsync(string factId, double confidence, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Reinforcing fact {FactId} via dedup (confidence={Confidence}).", factId, confidence);
        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.MarkDeduplicated, new { id = factId, confidence });
            var record = await cursor.SingleAsync();
            var node = record["f"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task CreateExtractedFromRelationshipAsync(string factId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Fact {FactId} -> Message {MessageId}", factId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                FactQueries.CreateExtractedFrom,
                new { factId, messageId });
        }, cancellationToken);
    }

    public async Task CreateAboutRelationshipAsync(string factId, string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating ABOUT: Fact {FactId} -> Entity {EntityId}", factId, entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                FactQueries.CreateAbout,
                new { factId, entityId });
        }, cancellationToken);
    }

    public async Task CreateConversationFactRelationshipAsync(string conversationId, string factId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_FACT: Conversation {ConversationId} -> Fact {FactId}", conversationId, factId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                FactQueries.CreateConversationFact,
                new { conversationId, factId });
        }, cancellationToken);
    }

    private static Fact MapToFact(INode node, float[]? embedding) =>
        new()
        {
            FactId           = node["id"].As<string>(),
            Subject          = node["subject"].As<string>(),
            Predicate        = node["predicate"].As<string>(),
            Object           = node["object"].As<string>(),
            OwnerId          = node.Properties.TryGetValue("owner_id", out var oid) ? oid.As<string>() : null,
            Category         = node.Properties.TryGetValue("category", out var cat) ? cat.As<string>() : null,
            Confidence       = node["confidence"].As<double>(),
            ValidFrom        = node.Properties.TryGetValue("valid_from", out var vf)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(vf)
                                : null,
            ValidUntil       = node.Properties.TryGetValue("valid_until", out var vu)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(vu)
                                : null,
            Embedding        = embedding,
            SourceMessageIds = node.Properties.TryGetValue("source_message_ids", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc     = Neo4jDateTimeHelper.ReadDateTimeOffset(node["created_at"]),
            Metadata         = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    public async Task<PagedResult<Fact>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting up to {Limit} facts without embeddings", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.GetPageWithoutEmbedding, new { limit = limit + 1 });
            var records = await cursor.ToListAsync();
            var items = records.Select(r =>
            {
                var node = r["f"].As<INode>();
                return MapToFact(node, null);
            }).ToList();
            return PaginationHelper.ApplyPagination(items, limit);
        }, cancellationToken);
    }

    public async Task UpdateEmbeddingAsync(
        string factId,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        // Never overwrite with a zero-length vector — keep `embedding` NULL so the node stays
        // re-queueable for the back-fill rather than being poisoned with `[]`.
        if (embedding.Length == 0)
        {
            _logger.LogDebug("Skipping empty embedding update for fact {Id}.", factId);
            return;
        }

        _logger.LogDebug("Updating embedding for fact {Id}", factId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                FactQueries.UpdateEmbedding,
                new { id = factId, embedding = embedding.ToList() });
        }, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Deleting fact {Id}, owner={Owner}", factId, scope?.OwnerId);

        var cypher = FactQueries.Delete(hasOwner);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["factId"] = factId, ["ownerId"] = scope!.OwnerId! })
                : await runner.RunAsync(cypher, new { factId });
            var records = await cursor.ToListAsync();
            return records.Count > 0 && records[0]["deleted"].As<bool>();
        }, cancellationToken);
    }

    public async Task<bool> InvalidateAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Invalidating fact {Id}, owner={Owner}", factId, scope?.OwnerId);

        var cypher = FactQueries.Invalidate(hasOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["id"] = factId, ["now"] = now };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Count > 0 && records[0]["invalidated"].As<bool>();
        }, cancellationToken);
    }

    public async Task<bool> SupersedeAsync(string loserFactId, string winnerFactId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Superseding fact {Loser} with {Winner}, owner={Owner}", loserFactId, winnerFactId, scope?.OwnerId);

        var cypher = FactQueries.Supersede(hasOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["loserId"] = loserFactId, ["winnerId"] = winnerFactId, ["now"] = now };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Count > 0 && records[0]["superseded"].As<bool>();
        }, cancellationToken);
    }

    public async Task<Fact?> FindByTripleAsync(string subject, string predicate, string @object, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Finding fact by triple ({Subject}, {Predicate}, {Object}), owner={Owner}", subject, predicate, @object, scope?.OwnerId);

        var cypher = FactQueries.FindByTriple(hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["subject"] = subject, ["predicate"] = predicate, ["object"] = @object, ["ownerId"] = scope!.OwnerId! })
                : await runner.RunAsync(cypher, new { subject, predicate, @object });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["f"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = hasOwner ? Math.Max(limit * OwnerOverFetchFactor, limit + OwnerOverFetchFloor) : limit;
        // D6 bitemporal: asOf is the valid-time clock; systemAsOf is the transaction clock (defaults to asOf
        // for ordinary single-clock recall — identical to the previous behaviour).
        _logger.LogDebug("Temporal vector search facts valid@{ValidAsOf} system@{SystemAsOf}, limit={Limit}, owner={Owner}",
            asOf, systemAsOf ?? asOf, limit, scope?.OwnerId);

        var cypher = TemporalQueries.SearchFactsAsOf(hasOwner, includeShared, topK);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"]  = queryEmbedding.ToList(),
            ["limit"]      = limit,
            ["minScore"]   = minScore,
            ["validAsOf"]  = asOf.UtcDateTime.ToString("O"),
            ["systemAsOf"] = (systemAsOf ?? asOf).UtcDateTime.ToString("O")
        };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToFact(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }
}