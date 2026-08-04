using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jPreferenceRepository
{
    public async Task<IReadOnlyList<Preference>> UpsertFusedBatchAsync(
        IReadOnlyList<Preference> preferences,
        CancellationToken cancellationToken = default)
    {
        if (preferences.Count == 0) return Array.Empty<Preference>();

        _logger.LogDebug("Fused batch upserting {Count} preferences", preferences.Count);
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
            ["metadata"] = SerializeMetadata(preference.Metadata),
            ["embedding"] = preference.Embedding is { Length: > 0 }
                ? preference.Embedding.ToList() : null,
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FusedPersistenceQueries.PreferenceUpsertBatch, new { items })
                .ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var byId = preferences.ToDictionary(preference => preference.PreferenceId, StringComparer.Ordinal);
            return records.Select(record =>
            {
                var node = record["p"].As<INode>();
                var id = node["id"].As<string>();
                return MapToPreference(node, byId.TryGetValue(id, out var source)
                    ? source.Embedding : ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
}
