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

    /// <summary>
    /// Extracts every supported category from <see cref="ExtractionWindow.Targets"/>, reading
    /// <see cref="ExtractionWindow.Context"/> only to understand them (E2).
    /// </summary>
    /// <remarks>
    /// The default implementation ignores the context and calls the existing overload — byte-identical
    /// to pre-E2 behaviour. Implementers must not extract from context turns: doing so re-asserts
    /// stored facts and inflates confidence (S2) and <c>mention_count</c> (R7) on nothing but recency.
    /// </remarks>
    Task<UnifiedExtractionResult> ExtractWithContextAsync(
        ExtractionWindow window,
        CancellationToken cancellationToken = default)
        => ExtractAsync(window.Targets, cancellationToken);
}
