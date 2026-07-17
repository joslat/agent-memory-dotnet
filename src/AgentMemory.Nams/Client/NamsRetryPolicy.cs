using System.Net;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Nams.Client;

/// <summary>
/// Implements the engineering plan's Phase 2 retry matrix (dependency-free, mirroring the style of
/// <c>AgentMemory.Enrichment/Http/RetryHttpMessageHandler.cs</c> rather than taking a Polly dependency).
/// Idempotent operations (reads) retry transient failures with exponential backoff, honoring a <c>Retry-After</c>
/// header on 429. Non-idempotent operations (writes) make a single attempt -- <c>strategy/NAMS/Neo4j_Questions.md</c>
/// #12-15 (does NAMS support idempotency keys?) are unanswered, so retrying a write risks a duplicate.
/// </summary>
internal sealed class NamsRetryPolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly ILogger? _logger;

    public NamsRetryPolicy(int maxAttempts, TimeSpan baseDelay, ILogger? logger = null)
    {
        _maxAttempts = Math.Max(0, maxAttempts);
        _baseDelay = baseDelay;
        _logger = logger;
    }

    /// <summary><paramref name="requestFactory"/> is invoked once per attempt -- an <see cref="HttpRequestMessage"/>
    /// can only be sent once, so each retry needs a fresh instance. <paramref name="onRetry"/> (Phase 9), if
    /// supplied, is invoked once per actual retry -- after the transient response/exception is observed, right
    /// before the retry delay -- so a caller can record a metric without this policy taking a hard dependency
    /// on any metrics type.</summary>
    public async Task<HttpResponseMessage> ExecuteAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpClient httpClient,
        bool isIdempotent,
        CancellationToken cancellationToken,
        Action? onRetry = null)
    {
        if (!isIdempotent)
            return await httpClient.SendAsync(requestFactory(), cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await httpClient.SendAsync(requestFactory(), cancellationToken).ConfigureAwait(false);
                if (attempt >= _maxAttempts || !IsTransient(response.StatusCode))
                    return response;

                var delay = RetryDelayFor(response, attempt);
                _logger?.LogDebug(
                    "Transient NAMS response {Status}; retry {Next}/{Max} after {Delay}.",
                    (int)response.StatusCode, attempt + 1, _maxAttempts, delay);
                response.Dispose();
                onRetry?.Invoke();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // caller cancellation -- never retry
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && attempt < _maxAttempts)
            {
                // Network failure, or a per-request timeout (a TaskCanceledException whose token was NOT the
                // caller's -- caller cancellation is handled by the guarded catch above).
                _logger?.LogDebug(ex, "Transient NAMS failure; retry {Next}/{Max}.", attempt + 1, _maxAttempts);
                onRetry?.Invoke();
                await Task.Delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status is >= 500 and < 600;

    private TimeSpan RetryDelayFor(HttpResponseMessage response, int attempt)
    {
        // Retry-After has two legal forms (RFC 9110 §10.2.3): delta-seconds or an absolute HTTP-date. They're
        // mutually exclusive on RetryConditionHeaderValue -- check both, since NAMS's use of one form over the
        // other isn't confirmed (strategy/NAMS/Neo4j_Questions.md #24).
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is { } delta)
                return delta;
            if (retryAfter?.Date is { } date)
            {
                var untilDate = date - DateTimeOffset.UtcNow;
                return untilDate > TimeSpan.Zero ? untilDate : TimeSpan.Zero;
            }
        }

        return BackoffFor(attempt);
    }

    private TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
}
