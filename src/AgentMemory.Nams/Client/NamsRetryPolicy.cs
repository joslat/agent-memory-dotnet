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
    private readonly string? _secretToRedact;

    public NamsRetryPolicy(int maxAttempts, TimeSpan baseDelay, ILogger? logger = null, string? secretToRedact = null)
    {
        _maxAttempts = Math.Max(0, maxAttempts);
        _baseDelay = baseDelay;
        _logger = logger;
        _secretToRedact = secretToRedact;
    }

    /// <summary><paramref name="requestFactory"/> is invoked once per attempt -- an <see cref="HttpRequestMessage"/>
    /// can only be sent once, so each retry needs a fresh instance. <paramref name="onRetry"/> (Phase 9), if
    /// supplied, is invoked once per actual retry -- after the transient response/exception is observed, right
    /// before the retry delay -- so a caller can record a metric without this policy taking a hard dependency
    /// on any metrics type.</summary>
    public async Task<HttpResponseMessage> ExecuteAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpClient httpClient,
        NamsRetryEligibility retryEligibility,
        CancellationToken cancellationToken,
        Action? onRetry = null)
    {
        if (retryEligibility == NamsRetryEligibility.NonIdempotent)
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
                // A logger renders the passed exception via ToString(), which would bypass redaction if the
                // raw ex were passed directly (same gap NamsClientExceptionMapper.FromTransportException had,
                // and the same fix -- a redacted-message-only substitute, never the raw exception object).
                _logger?.LogDebug(
                    NamsClientExceptionMapper.RedactedInnerException(ex, _secretToRedact),
                    "Transient NAMS failure; retry {Next}/{Max}.", attempt + 1, _maxAttempts);
                onRetry?.Invoke();
                await Task.Delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status is >= 500 and < 600;

    // Internal (not private): lets a unit test verify the Retry-After capping directly.
    internal TimeSpan RetryDelayFor(HttpResponseMessage response, int attempt)
    {
        // Retry-After has two legal forms (RFC 9110 §10.2.3): delta-seconds or an absolute HTTP-date. They're
        // mutually exclusive on RetryConditionHeaderValue -- check both, since NAMS's use of one form over the
        // other isn't confirmed (strategy/NAMS/Neo4j_Questions.md #24).
        // Both forms are server-supplied and capped at MaxBackoff too -- an absurd or misconfigured
        // Retry-After (e.g. many days out) would otherwise bypass the cap entirely, since neither value
        // originates from BackoffFor's own formula. Both are also floored at zero before capping -- RFC 9110
        // forbids a negative delta-seconds value, but RetryConditionHeaderValue's own constructor doesn't
        // enforce that, and Task.Delay throws ArgumentOutOfRangeException for any negative TimeSpan.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is { } delta)
                return Cap(delta > TimeSpan.Zero ? delta : TimeSpan.Zero);
            if (retryAfter?.Date is { } date)
            {
                var untilDate = date - DateTimeOffset.UtcNow;
                return Cap(untilDate > TimeSpan.Zero ? untilDate : TimeSpan.Zero);
            }
        }

        return BackoffFor(attempt);
    }

    // A cap, not just a formula: MaxRetryAttempts only has a lower bound (>= 0) enforced by
    // NamsOptionValidator -- with no ceiling here, a misconfigured large attempt count would grow this
    // exponential past Task.Delay's ~49.7-day argument limit (ArgumentOutOfRangeException) or even overflow
    // TimeSpan.FromMilliseconds itself (OverflowException), both escaping this class's own
    // NamsOperationException/NamsFailureKind error contract as a raw, unclassified framework exception.
    // Internal (not private): lets a unit test verify the cap directly instead of waiting through real
    // multi-attempt delays.
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    internal TimeSpan BackoffFor(int attempt) => Cap(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));

    // NaN is reachable and not caught by an uncapped >= comparison alone: baseDelay has no validation floor
    // (unlike maxAttempts), so baseDelay=0 combined with a large enough attempt that Math.Pow(2, attempt)
    // itself overflows to +Infinity produces 0 * Infinity = NaN, and every comparison against NaN is false --
    // TimeSpan.FromMilliseconds(NaN) would then throw ArgumentException, the same class of bug this cap
    // exists to prevent. (+Infinity alone IS already caught by the >= comparison, since any comparison
    // against +Infinity other than NaN is well-defined under IEEE 754 -- no separate IsInfinity check needed.)
    private static TimeSpan Cap(double milliseconds) =>
        double.IsNaN(milliseconds) || milliseconds >= MaxBackoff.TotalMilliseconds
            ? MaxBackoff
            : TimeSpan.FromMilliseconds(milliseconds);

    private static TimeSpan Cap(TimeSpan delay) => delay >= MaxBackoff ? MaxBackoff : delay;
}
