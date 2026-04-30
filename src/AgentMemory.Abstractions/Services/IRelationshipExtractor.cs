using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

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
