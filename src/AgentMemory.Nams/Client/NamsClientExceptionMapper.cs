using System.Net;
using System.Text.Json;
using AgentMemory.Nams.Domain;

namespace AgentMemory.Nams.Client;

/// <summary>
/// Maps a NAMS HTTP response or transport-level exception into a <see cref="NamsOperationResult{T}"/> /
/// <see cref="NamsOperationException"/>. Every failure message is redacted against the configured API key before
/// it can propagate, per the Phase 2 test list's "redaction of tokens" requirement -- defense in depth in case a
/// server-side error response ever echoes back request details.
/// </summary>
internal static class NamsClientExceptionMapper
{
    public static async Task<NamsOperationResult<T>> MapResponseAsync<T>(
        HttpResponseMessage response,
        Func<Stream, CancellationToken, Task<T>> deserialize,
        string? secretToRedact,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var value = await deserialize(stream, cancellationToken).ConfigureAwait(false);
                return NamsOperationResult<T>.Success(value);
            }
            catch (JsonException ex)
            {
                return NamsOperationResult<T>.Failure(
                    NamsFailureKind.Validation,
                    Redact($"NAMS returned a response that could not be parsed: {ex.Message}", secretToRedact),
                    (int)response.StatusCode);
            }
        }

        var kind = ClassifyStatusCode(response.StatusCode);
        var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var message = Redact(
            $"NAMS request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}", secretToRedact);
        return NamsOperationResult<T>.Failure(kind, message, (int)response.StatusCode);
    }

    public static NamsOperationException ToException<T>(NamsOperationResult<T> failure) =>
        new(failure.FailureKind, failure.FailureMessage ?? "NAMS request failed.", failure.StatusCode);

    public static NamsOperationException FromTransportException(Exception ex, string? secretToRedact) => ex switch
    {
        HttpRequestException => new NamsOperationException(
            NamsFailureKind.Network, Redact($"Network failure calling NAMS: {ex.Message}", secretToRedact), innerException: ex),
        TaskCanceledException => new NamsOperationException(
            NamsFailureKind.Timeout, "NAMS request timed out.", innerException: ex),
        _ => new NamsOperationException(
            NamsFailureKind.Unknown, Redact($"Unexpected failure calling NAMS: {ex.Message}", secretToRedact), innerException: ex)
    };

    private static NamsFailureKind ClassifyStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => NamsFailureKind.Validation,
        HttpStatusCode.Unauthorized => NamsFailureKind.Authentication,
        HttpStatusCode.Forbidden => NamsFailureKind.Authorization,
        HttpStatusCode.NotFound => NamsFailureKind.NotFound,
        HttpStatusCode.TooManyRequests => NamsFailureKind.RateLimited,
        _ when (int)statusCode >= 500 => NamsFailureKind.ServerError,
        _ => NamsFailureKind.Unknown
    };

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        {
            return "<unreadable body>";
        }
    }

    private static string Redact(string text, string? secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
}
