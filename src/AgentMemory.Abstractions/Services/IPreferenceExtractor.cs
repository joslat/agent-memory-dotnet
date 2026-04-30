using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for preferences from text.
/// </summary>
public interface IPreferenceExtractor
{
    /// <summary>
    /// Extracts preferences from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
