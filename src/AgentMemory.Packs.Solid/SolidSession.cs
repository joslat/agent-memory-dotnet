using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebIdGrammar = AgentMemory.Packs.Format.WebId;

namespace AgentMemory.Packs.Solid;

/// <summary>A response read whole, with every value of every header (a resource has several Link headers).</summary>
internal sealed record SolidResponse(HttpStatusCode Status, string Url, IReadOnlyDictionary<string, IReadOnlyList<string>> Headers, byte[] Body)
{
    internal bool IsSuccess => (int)Status is >= 200 and < 300;

    internal IEnumerable<string> Values(string header) => Headers.TryGetValue(header, out var values) ? values : [];

    /// <summary>The targets of the Link headers with <paramref name="rel"/>, resolved against the response URL.</summary>
    internal IEnumerable<string> Links(string rel)
    {
        foreach (var value in Values("Link"))
            foreach (var part in SplitLinks(value))
            {
                var close = part.IndexOf('>', StringComparison.Ordinal);
                if (!part.StartsWith('<') || close < 0) continue;
                var parameters = part[(close + 1)..];
                if (parameters.Split(';').Any(p => p.Trim() is var t && (t == $"rel=\"{rel}\"" || t == $"rel={rel}")))
                    yield return Iris.Resolve(Url, part[1..close]);
            }
    }

    private static IEnumerable<string> SplitLinks(string value)
    {
        var start = 0;
        var inside = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '<') inside = true;
            else if (value[i] == '>') inside = false;
            else if (value[i] == ',' && !inside) { yield return value[start..i].Trim(); start = i + 1; }
        }
        yield return value[start..].Trim();
    }
}

/// <summary>What a Solid session may reach and read.</summary>
internal sealed record SolidOptions
{
    /// <summary>Plain http only for loopback hosts (a local test server); https everywhere else, always.</summary>
    internal bool AllowLoopbackHttp { get; init; }
    /// <summary>The largest RDF document read: profiles, type indexes, catalogs.</summary>
    internal int MaximumDocumentBytes { get; init; } = 1 << 20;
    /// <summary>The largest archive read: the format's own archive bound.</summary>
    internal long MaximumArchiveBytes { get; init; } = 512L << 20;
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// A session with Solid servers (federated packs §7): anonymous, or logged in as a WebID with Solid-OIDC client credentials.
/// Tokens are DPoP-bound, each request carries a fresh ES256 proof (htm, htu, iat, jti, ath). Redirects are followed only
/// within the same origin; plain http only for loopback when the options allow it; bodies are bounded before they are read.
/// The client secret is used once, for the token, and kept nowhere else.
/// </summary>
internal sealed class SolidSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ECDsa _dpopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _jwk;
    private string? _accessToken;
    private DateTimeOffset _expires;
    private Func<CancellationToken, Task<(string Token, DateTimeOffset Expires)>>? _renew;

    internal SolidOptions Options { get; }
    /// <summary>The WebID this session acts as; null when anonymous.</summary>
    internal string? WebId { get; private set; }

    private SolidSession(HttpClient? http, SolidOptions options)
    {
        Options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = options.Timeout };
        var q = _dpopKey.ExportParameters(false).Q;
        _jwk = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.Encode(q.X!)}\",\"y\":\"{Base64Url.Encode(q.Y!)}\"}}";
    }

    internal static SolidSession Anonymous(SolidOptions? options = null, HttpClient? http = null) => new(http, options ?? new SolidOptions());

    /// <summary>Logs in as <paramref name="webId"/> with client credentials registered for it at its OIDC issuer.</summary>
    internal static async Task<SolidSession> LoginAsync(string webId, string clientId, string clientSecret, SolidOptions? options = null,
        HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        if (WebIdGrammar.Problem(webId, "The WebID") is { } problem) throw new InvalidDataException(problem);
        var session = new SolidSession(http, options ?? new SolidOptions());
        try
        {
            var profile = await session.ProfileAsync(webId, cancellationToken).ConfigureAwait(false);
            if (profile.Issuers.Count == 0) throw new InvalidDataException($"{webId} names no solid:oidcIssuer.");
            var issuer = profile.Issuers[0];
            session.CheckUrl(issuer);
            var discovery = await session.SendAsync(HttpMethod.Get, issuer.TrimEnd('/') + "/.well-known/openid-configuration", accept: "application/json",
                limit: session.Options.MaximumDocumentBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!discovery.IsSuccess) throw new InvalidDataException($"The issuer {issuer} gave {(int)discovery.Status} for its configuration.");
            using var configuration = JsonDocument.Parse(discovery.Body);
            var tokenEndpoint = configuration.RootElement.GetProperty("token_endpoint").GetString() ?? throw new InvalidDataException("The issuer names no token endpoint.");
            session.CheckUrl(tokenEndpoint);
            if (Origin(tokenEndpoint) != Origin(issuer)) throw new InvalidDataException("The token endpoint is on another origin than the issuer.");
            session._renew = ct => session.TokenAsync(tokenEndpoint, clientId, clientSecret, ct);
            (session._accessToken, session._expires) = await session._renew(cancellationToken).ConfigureAwait(false);
            session.WebId = webId;
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task<(string Token, DateTimeOffset Expires)> TokenAsync(string tokenEndpoint, string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", "webid")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(clientSecret))));
        request.Headers.Add("DPoP", Proof("POST", tokenEndpoint, null));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await Bounded(response, Options.MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
        // Never echo the response: a token endpoint's error may repeat what it was sent.
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"The token endpoint refused the client credentials ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (!root.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "DPoP", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The token endpoint did not issue a DPoP-bound token.");
        var token = root.GetProperty("access_token").GetString() ?? throw new InvalidDataException("The token endpoint issued no token.");
        var lifetime = root.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var s) ? s : 300;
        return (token, DateTimeOffset.UtcNow.AddSeconds(lifetime));
    }

    /// <summary>Reads and parses a WebID's profile document (anonymously, as anyone may).</summary>
    internal async Task<WebIdProfile> ProfileAsync(string webId, CancellationToken cancellationToken = default)
    {
        if (WebIdGrammar.Problem(webId, "The WebID") is { } problem) throw new InvalidDataException(problem);
        var document = webId.Contains('#', StringComparison.Ordinal) ? webId[..webId.IndexOf('#', StringComparison.Ordinal)] : webId;
        var response = await SendAsync(HttpMethod.Get, document, accept: "text/turtle", limit: Options.MaximumDocumentBytes, anonymous: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) throw new InvalidDataException($"The profile of {webId} gave {(int)response.Status}.");
        return WebIdProfile.Read(webId, Turtle.Parse(Text(response), response.Url));
    }

    /// <summary>Reads an RDF document as Turtle; null when it is not there or not readable by this session.</summary>
    internal async Task<IReadOnlyList<RdfTriple>?> TurtleAsync(string url, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, url, accept: "text/turtle", limit: Options.MaximumDocumentBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return null;
        if (!response.IsSuccess) throw new InvalidDataException($"{url} gave {(int)response.Status}.");
        return Turtle.Parse(Text(response), response.Url);
    }

    internal static string Text(SolidResponse response)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(response.Body); }
        catch (DecoderFallbackException) { throw new InvalidDataException($"{response.Url} is not UTF-8."); }
    }

    /// <summary>
    /// One request: authenticated with a fresh DPoP proof unless anonymous; same-origin redirects only (at most three); the
    /// body read to at most <paramref name="limit"/> bytes, or refused.
    /// </summary>
    internal async Task<SolidResponse> SendAsync(HttpMethod method, string url, byte[]? body = null, string? contentType = null, string? accept = null,
        long limit = 1 << 20, bool anonymous = false, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        CheckUrl(url);
        var origin = Origin(url);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (contentType is not null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            if (accept is not null) request.Headers.Accept.ParseAdd(accept);
            foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
            if (!anonymous && _renew is not null)
            {
                if (_expires - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(30)) (_accessToken, _expires) = await _renew(cancellationToken).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", _accessToken);
                request.Headers.Add("DPoP", Proof(method.Method, url, _accessToken));
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                var next = Iris.Resolve(url, location.OriginalString);
                if (hop >= 3 || Origin(next) != origin) throw new InvalidDataException($"{url} redirects to another origin or too often; refused.");
                if ((int)response.StatusCode == 303) { method = HttpMethod.Get; body = null; }
                url = next;
                continue;
            }
            var headerValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in response.Headers) headerValues[name] = [.. values];
            foreach (var (name, values) in response.Content.Headers) headerValues[name] = [.. values];
            var bytes = method == HttpMethod.Head ? [] : await Bounded(response, limit, cancellationToken).ConfigureAwait(false);
            return new SolidResponse(response.StatusCode, url, headerValues, bytes);
        }
    }

    private static async Task<byte[]> Bounded(HttpResponseMessage response, long limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > limit) throw new InvalidDataException($"{response.RequestMessage?.RequestUri} is larger than {limit:N0} bytes.");
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var disposeStream = stream.ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException($"{response.RequestMessage?.RequestUri} is larger than {limit:N0} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>A DPoP proof (RFC 9449): ES256 over a header naming this session's key and the request's method and URL.</summary>
    private string Proof(string method, string url, string? accessToken)
    {
        var htu = url.Split('#')[0].Split('?')[0];
        var header = $"{{\"alg\":\"ES256\",\"jwk\":{_jwk},\"typ\":\"dpop+jwt\"}}";
        var claims = new StringBuilder("{");
        if (accessToken is not null) claims.Append("\"ath\":\"").Append(Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)))).Append("\",");
        claims.Append("\"htm\":\"").Append(method).Append("\",\"htu\":").Append(JsonSerializer.Serialize(htu))
            .Append(",\"iat\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Append(",\"jti\":\"").Append(Guid.NewGuid().ToString("N")).Append("\"}");
        var signing = Base64Url.Encode(Encoding.UTF8.GetBytes(header)) + "." + Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToString()));
        var signature = _dpopKey.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signing + "." + Base64Url.Encode(signature);
    }

    /// <summary>https only, or http to a loopback host when the options allow it; no user information.</summary>
    internal void CheckUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && Options.AllowLoopbackHttp))
            throw new InvalidDataException($"{url} is not an https URL{(Options.AllowLoopbackHttp ? " (or http to this machine)" : "")}; refused.");
    }

    internal static string Origin(string url)
    {
        var uri = new Uri(url);
        return uri.Scheme + "://" + uri.Authority;
    }

    public void Dispose()
    {
        _dpopKey.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
