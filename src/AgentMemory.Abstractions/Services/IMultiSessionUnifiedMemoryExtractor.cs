using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Optionally extracts typed memory for several source sessions in token-bounded model requests.
/// Every returned result is keyed to exactly one input session so provenance cannot bleed across
/// session or owner boundaries.
/// </summary>
public interface IMultiSessionUnifiedMemoryExtractor
{
    /// <summary>Whether the extractor is explicitly enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Extracts the supplied requests using contiguous batches no larger than
    /// <paramref name="maxSessionsPerBatch"/> or <paramref name="maxInputTokens"/>.
    /// Implementations must return exactly one result for every unique input session.
    /// </summary>
    Task<IReadOnlyDictionary<string, UnifiedExtractionResult>> ExtractAsync(
        IReadOnlyList<ExtractionRequest> requests,
        int maxSessionsPerBatch,
        int maxInputTokens,
        CancellationToken cancellationToken = default);
}
