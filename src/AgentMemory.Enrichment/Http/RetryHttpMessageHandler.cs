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

    private TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
}
