using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for facts from text.
/// </summary>
public interface IFactExtractor
{
    /// <summary>
    /// Extracts facts from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
