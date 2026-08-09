using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>Optionally extracts all memory categories in one provider call.</summary>
public interface IUnifiedMemoryExtractor
{
    /// <summary>Whether this extractor should replace the category-specific fan-out.</summary>
    bool IsEnabled { get; }

    /// <summary>Extracts every supported category from the supplied messages.</summary>
    Task<UnifiedExtractionResult> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
