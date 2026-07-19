using System.Net;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Enrichment.Http;

/// <summary>
/// A dependency-free <see cref="DelegatingHandler"/> that retries transient HTTP failures up to a
/// configured number of times with exponential backoff. This wires the <c>MaxRetries</c> option of the
/// Nominatim/Wikimedia clients without taking a Polly dependency.
/// </summary>
/// <remarks>
/// <para>A failure is treated as transient when it is a network error (<see cref="HttpRequestException"/>),
/// a request timeout (a <see cref="TaskCanceledException"/> whose token is <i>not</i> the caller's — i.e.
/// the per-request <c>HttpClient.Timeout</c> fired), or a retryable status code (408, 429, 500, 502, 503,
/// 504). Caller cancellation is never retried.</para>
/// <para>Intended for the idempotent, body-less GET clients here; the same request instance is re-sent,
/// which the runtime permits for requests without (non-rewindable) content.</para>
/// </remarks>
internal sealed class RetryHttpMessageHandler : DelegatingHandler
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly ILogger? _logger;

    public RetryHttpMessageHandler(int maxRetries, ILogger? logger = null, TimeSpan? baseDelay = null)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(500);
        _logger = logger;
    }

    private static bool IsTransient(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,        // 408
        HttpStatusCode.TooManyRequests => true,       // 429
        HttpStatusCode.InternalServerError => true,   // 500
        HttpStatusCode.BadGateway => true,            // 502
        HttpStatusCode.ServiceUnavailable => true,    // 503
        HttpStatusCode.GatewayTimeout => true,        // 504
        _ => false
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (attempt >= _maxRetries || !IsTransient(response.StatusCode))
                    return response;

                _logger?.LogDebug(
                    "Transient HTTP {Status} from {Uri}; retry {Next}/{Max}.",
                    (int)response.StatusCode, request.RequestUri, attempt + 1, _maxRetries);
                response.Dispose();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // caller cancellation — never retry
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && attempt < _maxRetries)
            {
                // Network failure, or a per-request timeout (a TaskCanceledException whose token was NOT
                // the caller's — caller cancellation is handled by the guarded catch above). Retry.
                _logger?.LogDebug(ex,
                    "Transient HTTP failure to {Uri}; retry {Next}/{Max}.",
                    request.RequestUri, attempt + 1, _maxRetries);
            }

            await Task.Delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    // A cap, not just a formula: maxRetries only has a lower bound (Math.Max(0, ...) above) enforced by the
    // ctor -- with no ceiling here, a caller-configured large retry count would grow this exponential past
    // Task.Delay's ~49.7-day argument limit (ArgumentOutOfRangeException) or even overflow
    // TimeSpan.FromMilliseconds itself (OverflowException), both escaping as a raw, unhandled exception
    // instead of the graceful retry behavior this handler exists to provide. Same bug found and fixed in the
    // NAMS backend's own mirror of this class, AgentMemory.Nams.Client.NamsRetryPolicy.
    // Internal (not private): lets a unit test verify the cap directly instead of waiting through real
    // multi-attempt delays.
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    // NaN is reachable and not caught by an uncapped >= comparison alone: baseDelay has no validation floor
    // (unlike maxRetries), so baseDelay=0 combined with a large enough attempt that Math.Pow(2, attempt)
    // itself overflows to +Infinity produces 0 * Infinity = NaN, and every comparison against NaN is false --
    // TimeSpan.FromMilliseconds(NaN) would then throw ArgumentException, the same class of bug this cap
    // exists to prevent. (+Infinity alone IS already caught by the >= comparison, since any comparison
    // against +Infinity other than NaN is well-defined under IEEE 754 -- no separate IsInfinity check needed.)
    internal TimeSpan BackoffFor(int attempt)
    {
        var uncapped = _baseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        return double.IsNaN(uncapped) || uncapped >= MaxBackoff.TotalMilliseconds
            ? MaxBackoff
            : TimeSpan.FromMilliseconds(uncapped);
    }
}
