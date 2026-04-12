using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for relationships from text.
/// </summary>
public interface IRelationshipExtractor
{
    /// <summary>
    /// Extracts relationships from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
