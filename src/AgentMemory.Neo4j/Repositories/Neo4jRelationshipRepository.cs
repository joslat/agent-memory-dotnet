using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed class Neo4jRelationshipRepository : IRelationshipRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jRelationshipRepository> _logger;

    public Neo4jRelationshipRepository(INeo4jTransactionRunner tx, ILogger<Neo4jRelationshipRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<Relationship> UpsertAsync(Relationship relationship, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting relationship {Id} ({Source}→{Target})",
            relationship.RelationshipId, relationship.SourceEntityId, relationship.TargetEntityId);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"]               = relationship.RelationshipId,
                ["sourceEntityId"]   = relationship.SourceEntityId,
                ["targetEntityId"]   = relationship.TargetEntityId,
                ["relationType"]     = relationship.RelationshipType,
                ["ownerId"]          = (object?)relationship.OwnerId,
                ["confidence"]       = relationship.Confidence,
                ["description"]      = (object?)relationship.Description,
                ["validFrom"]        = (object?)(relationship.ValidFrom?.ToString("O")),
                ["validUntil"]       = (object?)(relationship.ValidUntil?.ToString("O")),
                ["attributes"]       = SerializeMetadata(relationship.Attributes),
                ["sourceMessageIds"] = relationship.SourceMessageIds.ToList(),
                ["createdAt"]        = relationship.CreatedAtUtc.ToString("O"),
                ["updatedAt"]        = DateTimeOffset.UtcNow.ToString("O"),
                ["metadata"]         = SerializeMetadata(relationship.Metadata)
            };

            var cursor = await runner.RunAsync(RelationshipQueries.Upsert, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return MapToRelationship(record["r"].As<IRelationship>());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Relationship?> GetByIdAsync(string relationshipId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting relationship {Id}", relationshipId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(RelationshipQueries.GetById, new { id = relationshipId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            return MapToRelationship(records[0]["r"].As<IRelationship>());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Relationship>> GetByEntityAsync(
        string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting relationships for entity {EntityId}, owner={Owner}", entityId, scope?.OwnerId);

        var cypher = RelationshipQueries.GetByEntity(hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["entityId"] = entityId };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r => MapToRelationship(r["r"].As<IRelationship>())).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Relationship>> GetBySourceEntityAsync(
        string sourceEntityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting outgoing relationships for entity {EntityId}, owner={Owner}", sourceEntityId, scope?.OwnerId);

        var cypher = RelationshipQueries.GetBySourceEntity(hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["sourceEntityId"] = sourceEntityId };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r => MapToRelationship(r["r"].As<IRelationship>())).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Relationship>> GetByTargetEntityAsync(
        string targetEntityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting incoming relationships for entity {EntityId}, owner={Owner}", targetEntityId, scope?.OwnerId);

        var cypher = RelationshipQueries.GetByTargetEntity(hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["targetEntityId"] = targetEntityId };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r => MapToRelationship(r["r"].As<IRelationship>())).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Relationship MapToRelationship(IRelationship r) =>
        new()
        {
            RelationshipId   = r["id"].As<string>(),
            SourceEntityId   = r["source_entity_id"].As<string>(),
            TargetEntityId   = r["target_entity_id"].As<string>(),
            RelationshipType = r["relation_type"].As<string>(),
            OwnerId          = r.Properties.TryGetValue("owner_id", out var oid) ? oid.As<string?>() : null,
            Confidence       = r["confidence"].As<double>(),
            Description      = r.Properties.TryGetValue("description", out var desc) ? desc.As<string>() : null,
            ValidFrom        = r.Properties.TryGetValue("valid_from", out var vf)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(vf)
                                : null,
            ValidUntil       = r.Properties.TryGetValue("valid_until", out var vu)
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(vu)
                                : null,
            Attributes       = DeserializeMetadata(r.Properties.TryGetValue("attributes", out var attr) ? attr.As<string>() : null),
            SourceMessageIds = r.Properties.TryGetValue("source_message_ids", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc     = Neo4jDateTimeHelper.ReadDateTimeOffset(r["created_at"]),
            Metadata         = DeserializeMetadata(r.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };
}
