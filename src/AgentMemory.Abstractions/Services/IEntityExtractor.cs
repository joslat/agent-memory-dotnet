using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

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
