using Microsoft.Extensions.Options;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Process-local provider-batch limiter shared by all scoped multi-session extractors in one
/// service provider. A zero configured limit keeps the historical uncapped cross-scope behavior.
/// </summary>
internal sealed class LlmExtractionBatchConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim? _gate;

    public LlmExtractionBatchConcurrencyLimiter(
        IOptions<LlmExtractionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var maximum = options.Value.MaxConcurrentExtractionBatches;
        if (maximum > 0)
            _gate = new SemaphoreSlim(maximum, maximum);
    }

    internal async Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_gate is null)
            return await operation().ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate?.Dispose();
}
