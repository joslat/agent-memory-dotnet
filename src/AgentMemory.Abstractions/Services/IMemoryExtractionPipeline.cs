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

    /// <summary>
    /// Extracts several independent source sessions with token-bounded unified model calls, then
    /// resolves and persists each session chronologically. The returned results follow commit order.
    /// When no batch extractor is enabled, requests fall back to ordinary one-session extraction.
    /// </summary>
    async Task<IReadOnlyList<ExtractionResult>> ExtractBatchAsync(
        IReadOnlyList<ExtractionRequest> requests,
        int maxSessionsPerBatch,
        int maxInputTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputTokens);

        var ordered = requests
            .Select((request, index) => new
            {
                Request = request,
                Index = index,
                Timestamp = request.Messages
                    .Select(message => message.TimestampUtc)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Min(),
            })
            .OrderBy(item => item.Timestamp)
            .ThenBy(item => item.Index)
            .Select(item => item.Request)
            .ToArray();

        var results = new List<ExtractionResult>(ordered.Length);
        foreach (var request in ordered)
            results.Add(await ExtractAsync(request, cancellationToken).ConfigureAwait(false));
        return results;
    }
}
