using System.Net;
using System.Net.Http.Headers;

namespace AgentMemory.Nams.Authentication;

/// <summary>
/// Attaches <c>Authorization: Bearer &lt;token&gt;</c> to every outgoing NAMS request. On a 401, invalidates the
/// token, fetches a fresh one, and retries the request exactly once -- safe even for write operations, since a
/// 401 means the server never authenticated the caller and therefore never executed the operation. A 403 is
/// never retried here; it is a valid-credential authorization failure, not a token problem, and is left for
/// <see cref="Client.NamsClientExceptionMapper"/> to classify.
/// </summary>
internal sealed class NamsAuthenticationHandler : DelegatingHandler
{
    private readonly INamsAccessTokenProvider _tokenProvider;

    public NamsAuthenticationHandler(INamsAccessTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var bufferedContent = request.Content is null ? null : await BufferAsync(request.Content, cancellationToken).ConfigureAwait(false);

        var response = await SendWithTokenAsync(request, token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        await _tokenProvider.InvalidateAsync(token.Fingerprint, cancellationToken).ConfigureAwait(false);
        var refreshedToken = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);

        var retryRequest = Clone(request, bufferedContent);
        return await SendWithTokenAsync(retryRequest, refreshedToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(
        HttpRequestMessage request, NamsAccessToken token, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> BufferAsync(HttpContent content, CancellationToken cancellationToken) =>
        await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? bufferedContent)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (bufferedContent is not null)
        {
            clone.Content = new ByteArrayContent(bufferedContent);
            if (original.Content is not null)
                foreach (var header in original.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
