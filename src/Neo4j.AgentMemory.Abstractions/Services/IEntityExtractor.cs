using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for entities from text.
/// </summary>
public interface IEntityExtractor
{
    /// <summary>
    /// Extracts entities from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
