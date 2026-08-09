using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>A deterministic, content-local batch boundary produced before provider work begins.</summary>
public sealed record MultiSessionExtractionBatchPlan(
    IReadOnlyList<string> SourceSessionIds,
    int EstimatedInputTokens);

/// <summary>The complete provider-call plan for a multi-session extraction workload.</summary>
public sealed record MultiSessionExtractionPlan(
    IReadOnlyList<MultiSessionExtractionBatchPlan> Batches)
{
    /// <summary>Number of provider calls before validation retries or recursive splits.</summary>
    public int BatchCount => Batches.Count;

    /// <summary>Number of unique source sessions acknowledged by the plan.</summary>
    public int SourceSessionCount => Batches.Sum(batch => batch.SourceSessionIds.Count);

    /// <summary>Sum of the conservative per-batch input estimates.</summary>
    public long TotalEstimatedInputTokens => Batches.Sum(batch => (long)batch.EstimatedInputTokens);
}

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
    /// Produces the exact stable partition that execution will use before any provider call.
    /// Implementations that cannot expose a deterministic plan may retain the default failure;
    /// callers that require preflight must fail closed rather than estimate.
    /// </summary>
    MultiSessionExtractionPlan Plan(
        IReadOnlyList<ExtractionRequest> requests,
        int maxSessionsPerBatch,
        int maxInputTokens) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not expose a deterministic multi-session extraction plan.");

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
