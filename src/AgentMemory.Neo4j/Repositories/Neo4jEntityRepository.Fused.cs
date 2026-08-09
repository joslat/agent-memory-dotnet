using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jEntityRepository
{
    public async Task<IReadOnlyList<Entity>> UpsertFusedBatchAsync(
        IReadOnlyList<Entity> entities,
        CancellationToken cancellationToken = default)
    {
        if (entities.Count == 0) return Array.Empty<Entity>();

        _logger.LogDebug("Fused batch upserting {Count} entities", entities.Count);
        var items = entities.Select(entity => new Dictionary<string, object?>
        {
            ["id"] = entity.EntityId,
            ["owner_id"] = entity.OwnerId,
            ["name"] = entity.Name,
            ["canonical_name"] = entity.CanonicalName,
            ["type"] = entity.Type,
            ["subtype"] = entity.Subtype,
            ["description"] = entity.Description,
            ["confidence"] = entity.Confidence,
            ["aliases"] = entity.Aliases.ToList(),
            ["attributes"] = SerializeMetadata(entity.Attributes),
            ["source_message_ids"] = entity.SourceMessageIds.ToList(),
            ["created_at"] = entity.CreatedAtUtc.ToString("O"),
            ["metadata"] = SerializeMetadata(entity.Metadata),
            ["embedding"] = entity.Embedding is { Length: > 0 } ? entity.Embedding.ToList() : null,
            ["latitude"] = entity.Latitude,
            ["longitude"] = entity.Longitude,
            ["labels"] = BuildDynamicLabels(entity.Type, entity.Subtype),
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FusedPersistenceQueries.EntityUpsertBatch, new { items })
                .ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var byId = entities.ToDictionary(entity => entity.EntityId, StringComparer.Ordinal);
            return records.Select(record =>
            {
                var node = record["e"].As<INode>();
                var id = node["id"].As<string>();
                if (!byId.TryGetValue(id, out var source))
                    return MapToEntity(node, ReadEmbedding(node));
                return MapToEntity(node, source.Embedding) with
                {
                    Latitude = source.Latitude,
                    Longitude = source.Longitude,
                };
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
}
