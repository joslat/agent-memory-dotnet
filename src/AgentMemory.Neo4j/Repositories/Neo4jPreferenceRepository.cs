using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

public sealed class Neo4jPreferenceRepository : IPreferenceRepository
{
    private const int OwnerOverFetchFactor = Neo4jFactRepository.OwnerOverFetchFactor;
    private const int OwnerOverFetchFloor = Neo4jFactRepository.OwnerOverFetchFloor;

    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jPreferenceRepository> _logger;

    public Neo4jPreferenceRepository(INeo4jTransactionRunner tx, ILogger<Neo4jPreferenceRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<Preference> UpsertAsync(Preference preference, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting preference {Id}", preference.PreferenceId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"]               = preference.PreferenceId,
                ["ownerId"]          = preference.OwnerId,
                ["category"]         = preference.Category,
                ["preferenceText"]   = preference.PreferenceText,
                ["context"]          = (object?)preference.Context,
                ["confidence"]       = preference.Confidence,
                ["sourceMessageIds"] = preference.SourceMessageIds.ToList(),
                ["createdAtUtc"]     = preference.CreatedAtUtc.ToString("O"),
                ["metadata"]         = SerializeMetadata(preference.Metadata)
            };

            var cursor = await runner.RunAsync(PreferenceQueries.Upsert, parameters);
            var record = await cursor.SingleAsync();
            var node = record["p"].As<INode>();

            if (preference.Embedding is not null)
            {
                await runner.RunAsync(
                    PreferenceQueries.SetEmbedding,
                    new { id = preference.PreferenceId, embedding = preference.Embedding.ToList() });
            }

            // Auto-create EXTRACTED_FROM relationships for all source messages
            if (preference.SourceMessageIds.Count > 0)
            {
                await runner.RunAsync(
                    PreferenceQueries.CreateExtractedFromMessages,
                    new { id = preference.PreferenceId, sourceMessageIds = preference.SourceMessageIds.ToList() });
            }

            return MapToPreference(node, preference.Embedding);
        }, cancellationToken);
    }

    public async Task<Preference?> GetByIdAsync(string preferenceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting preference {Id}", preferenceId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PreferenceQueries.GetById, new { id = preferenceId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["p"].As<INode>();
            return MapToPreference(node, ReadEmbedding(node));
        }, cancellationToken);
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
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["p"].As<INode>();
                return MapToPreference(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = hasOwner ? Math.Max(limit * OwnerOverFetchFactor, limit + OwnerOverFetchFloor) : limit;
        _logger.LogDebug("Vector search preferences, limit={Limit}, owner={Owner}", limit, scope?.OwnerId);

        var cypher = PreferenceQueries.SearchByVector(hasOwner, includeShared, topK);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["limit"] = limit,
            ["minScore"] = minScore,
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
                return (MapToPreference(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    private const int DedupOverFetch = 10;

    public async Task<Preference?> FindDuplicateAsync(
        string category, float[] embedding, string? ownerId, double threshold,
        CancellationToken cancellationToken = default)
    {
        bool ownerIsShared = string.IsNullOrEmpty(ownerId);
        var cypher = PreferenceQueries.FindDuplicate(DedupOverFetch, ownerIsShared);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = embedding.ToList(),
            ["threshold"] = threshold,
            ["category"]  = category,
        };
        if (!ownerIsShared) parameters["ownerId"] = ownerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["node"].As<INode>();
            return MapToPreference(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<Preference> MarkDeduplicatedAsync(string preferenceId, double confidence, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Reinforcing preference {Id} via dedup (confidence={Confidence}).", preferenceId, confidence);
        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PreferenceQueries.MarkDeduplicated, new { id = preferenceId, confidence });
            var record = await cursor.SingleAsync();
            var node = record["p"].As<INode>();
            return MapToPreference(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task DeleteAsync(string preferenceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting preference {Id}", preferenceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.Delete,
                new { id = preferenceId });
        }, cancellationToken);
    }

    public async Task CreateExtractedFromRelationshipAsync(string preferenceId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Preference {PreferenceId} -> Message {MessageId}", preferenceId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.CreateExtractedFromRelationship,
                new { preferenceId, messageId });
        }, cancellationToken);
    }

    public async Task CreateAboutRelationshipAsync(string preferenceId, string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating ABOUT: Preference {PreferenceId} -> Entity {EntityId}", preferenceId, entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.CreateAboutRelationship,
                new { preferenceId, entityId });
        }, cancellationToken);
    }

    public async Task CreateConversationPreferenceRelationshipAsync(string conversationId, string preferenceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_PREFERENCE: Conversation {ConversationId} -> Preference {PreferenceId}", conversationId, preferenceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.CreateConversationPreferenceRelationship,
                new { conversationId, preferenceId });
        }, cancellationToken);
    }

    private static Preference MapToPreference(INode node, float[]? embedding) =>
        new()
        {
            PreferenceId     = node["id"].As<string>(),
            OwnerId          = node.Properties.TryGetValue("owner_id", out var oid) ? oid.As<string>() : null,
            Category         = node["category"].As<string>(),
            PreferenceText   = node["preference"].As<string>(),
            Context          = node.Properties.TryGetValue("context", out var ctx) ? ctx.As<string>() : null,
            Confidence       = node["confidence"].As<double>(),
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

    public async Task<PagedResult<Preference>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting up to {Limit} preferences without embeddings", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PreferenceQueries.GetPageWithoutEmbedding, new { limit = limit + 1 });
            var records = await cursor.ToListAsync();
            var items = records.Select(r =>
            {
                var node = r["p"].As<INode>();
                return MapToPreference(node, null);
            }).ToList();
            return PaginationHelper.ApplyPagination(items, limit);
        }, cancellationToken);
    }

    public async Task UpdateEmbeddingAsync(
        string preferenceId,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating embedding for preference {Id}", preferenceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                PreferenceQueries.UpdateEmbedding,
                new { id = preferenceId, embedding = embedding.ToList() });
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = hasOwner ? Math.Max(limit * Neo4jFactRepository.OwnerOverFetchFactor, limit + Neo4jFactRepository.OwnerOverFetchFloor) : limit;
        _logger.LogDebug("Temporal vector search preferences as of {AsOf}, limit={Limit}, owner={Owner}", asOf, limit, scope?.OwnerId);

        var cypher = TemporalQueries.SearchPreferencesAsOf(hasOwner, includeShared, topK);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["limit"]     = limit,
            ["minScore"]  = minScore,
            ["asOf"]      = asOf.UtcDateTime.ToString("O")
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
                return (MapToPreference(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }
}