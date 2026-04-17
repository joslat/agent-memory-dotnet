using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

public sealed class Neo4jRelationshipRepository : IRelationshipRepository
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

            var cursor = await runner.RunAsync(RelationshipQueries.Upsert, parameters);
            var record = await cursor.SingleAsync();
            return MapToRelationship(record["r"].As<IRelationship>());
        }, cancellationToken);
    }

    public async Task<Relationship?> GetByIdAsync(string relationshipId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting relationship {Id}", relationshipId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(RelationshipQueries.GetById, new { id = relationshipId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            return MapToRelationship(records[0]["r"].As<IRelationship>());
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Relationship>> GetByEntityAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting relationships for entity {EntityId}", entityId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(RelationshipQueries.GetByEntity, new { entityId });
            var records = await cursor.ToListAsync();
            return records.Select(r => MapToRelationship(r["r"].As<IRelationship>())).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Relationship>> GetBySourceEntityAsync(string sourceEntityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting outgoing relationships for entity {EntityId}", sourceEntityId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(RelationshipQueries.GetBySourceEntity, new { sourceEntityId });
            var records = await cursor.ToListAsync();
            return records.Select(r => MapToRelationship(r["r"].As<IRelationship>())).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Relationship>> GetByTargetEntityAsync(string targetEntityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting incoming relationships for entity {EntityId}", targetEntityId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(RelationshipQueries.GetByTargetEntity, new { targetEntityId });
            var records = await cursor.ToListAsync();
            return records.Select(r => MapToRelationship(r["r"].As<IRelationship>())).ToList();
        }, cancellationToken);
    }

    private static Relationship MapToRelationship(IRelationship r) =>
        new()
        {
            RelationshipId   = r["id"].As<string>(),
            SourceEntityId   = r["source_entity_id"].As<string>(),
            TargetEntityId   = r["target_entity_id"].As<string>(),
            RelationshipType = r["relation_type"].As<string>(),
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

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}
