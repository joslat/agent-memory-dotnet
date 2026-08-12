using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;
using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jFactRepository
{
    public async Task<IReadOnlyList<Fact>> UpsertFusedBatchAsync(
        IReadOnlyList<Fact> facts,
        CancellationToken cancellationToken = default)
    {
        if (facts.Count == 0) return Array.Empty<Fact>();

        _logger.LogDebug("Fused batch upserting {Count} facts", facts.Count);
        var deduped = facts
            .GroupBy(fact => TripleKey(
                fact.Subject, fact.Predicate, fact.Object, fact.OwnerId ?? OwnerKeyShared))
            .Select(group => group.Last())
            .ToList();
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");

        // L11. The fact merge key is the composite {subject_key, predicate_key, object_key,
        // owner_key}, and it is now backed by a range index — so an oversized value stops being a
        // slow scan and becomes a driver failure from inside the write. Reject it here, naming the
        // property and the fact, rather than surfacing an opaque message from mid-batch.
        foreach (var fact in deduped)
        {
            IndexKeyBudget.EnsureCompositeIndexable(
                [
                    ("subject_key", MemoryTripleCanonicalizer.CanonicalValue(fact.Subject)),
                    ("predicate_key", MemoryTripleCanonicalizer.Canonical(fact.Predicate)),
                    ("object_key", MemoryTripleCanonicalizer.CanonicalValue(fact.Object)),
                    ("owner_key", fact.OwnerId ?? OwnerKeyShared)
                ],
                fact.FactId);
        }

        var items = deduped.Select(fact => new Dictionary<string, object?>
        {
            ["id"] = fact.FactId,
            ["subject"] = fact.Subject,
            ["predicate"] = fact.Predicate,
            // The fused batch writer is the path extraction actually uses; the non-fused Upsert
            // carried these keys while this one did not, so canonical identity never reached a real
            // cold build.
            ["subject_key"] = MemoryTripleCanonicalizer.CanonicalValue(fact.Subject),
            ["predicate_key"] = MemoryTripleCanonicalizer.Canonical(fact.Predicate),
            ["object_key"] = MemoryTripleCanonicalizer.CanonicalValue(fact.Object),
            ["object"] = fact.Object,
            ["owner_id"] = fact.OwnerId,
            ["owner_key"] = fact.OwnerId ?? OwnerKeyShared,
            ["category"] = fact.Category,
            ["confidence"] = fact.Confidence,
            ["valid_from"] = fact.ValidFrom?.ToString("O"),
            ["valid_until"] = fact.ValidUntil?.ToString("O"),
            ["source_message_ids"] = fact.SourceMessageIds.ToList(),
            ["created_at"] = fact.CreatedAtUtc.ToString("O"),
            ["updated_at"] = updatedAt,
            ["metadata"] = SerializeMetadata(fact.Metadata),
            ["embedding"] = fact.Embedding is { Length: > 0 } ? fact.Embedding.ToList() : null,
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                    FusedPersistenceQueries.FactUpsertBatch,
                    new { items, reinforceAlpha = _reinforceAlpha })
                .ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var embeddingByTriple = deduped.ToDictionary(
                fact => TripleKey(fact.Subject, fact.Predicate, fact.Object, fact.OwnerId ?? OwnerKeyShared),
                fact => fact.Embedding);
            return records.Select(record =>
            {
                var node = record["f"].As<INode>();
                var key = TripleKey(
                    node["subject"].As<string>(),
                    node["predicate"].As<string>(),
                    node["object"].As<string>(),
                    node["owner_key"].As<string>());
                return MapToFact(node, embeddingByTriple.TryGetValue(key, out var embedding) ? embedding : null);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
}
