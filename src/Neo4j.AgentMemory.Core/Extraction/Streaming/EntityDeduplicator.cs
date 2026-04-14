using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Core.Extraction.Streaming;

/// <summary>
/// Static helpers that deduplicate extracted entities and relationships across chunks,
/// keeping the highest-confidence entry for each normalised key.
/// </summary>
internal static class EntityDeduplicator
{
    /// <summary>
    /// Deduplicates <paramref name="entities"/> by normalised name + type, retaining the
    /// entry with the highest confidence when duplicates exist.
    /// </summary>
    internal static IReadOnlyList<ExtractedEntity> DeduplicateEntities(
        IReadOnlyList<ExtractedEntity> entities)
    {
        var best = new Dictionary<string, ExtractedEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in entities)
        {
            string key = $"{entity.Name.Trim().ToUpperInvariant()}::{entity.Type.Trim().ToUpperInvariant()}";
            if (!best.TryGetValue(key, out var existing) || entity.Confidence > existing.Confidence)
                best[key] = entity;
        }

        return best.Values.ToList();
    }

    /// <summary>
    /// Deduplicates <paramref name="relationships"/> by
    /// (source, type, target) case-insensitively, retaining the highest-confidence entry.
    /// </summary>
    internal static IReadOnlyList<ExtractedRelationship> DeduplicateRelationships(
        IReadOnlyList<ExtractedRelationship> relationships)
    {
        var best = new Dictionary<(string, string, string), ExtractedRelationship>();

        foreach (var rel in relationships)
        {
            var key = (
                rel.SourceEntity.Trim().ToLowerInvariant(),
                rel.RelationshipType.Trim().ToLowerInvariant(),
                rel.TargetEntity.Trim().ToLowerInvariant()
            );

            if (!best.TryGetValue(key, out var existing) || rel.Confidence > existing.Confidence)
                best[key] = rel;
        }

        return best.Values.ToList();
    }
}
