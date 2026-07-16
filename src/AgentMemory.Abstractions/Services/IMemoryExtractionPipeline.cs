using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Pipeline for extracting structured memory from messages.
/// </summary>
public interface IMemoryExtractionPipeline
{
    /// <summary>
    /// Extracts structured memory from messages. The returned <see cref="ExtractionResult"/> carries
    /// structured per-item outcomes and an overall <see cref="IngestionStatus"/> (#101). Under the default
    /// <c>ExtractionOptions.FailureMode</c> (<c>IngestionFailureMode.BestEffort</c>) this never throws for
    /// a per-item failure; under <c>IngestionFailureMode.FailFast</c> it throws
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryIngestionException"/> at the first one,
    /// carrying every outcome completed before the failure.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);
}
