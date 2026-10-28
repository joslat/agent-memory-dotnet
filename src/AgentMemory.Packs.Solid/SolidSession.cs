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
/// The token must name that WebID (its <c>webid</c> claim), and the issuer must be the one the WebID's profile names. Tokens
/// are DPoP-bound; each request carries a fresh ES256 proof (htm, htu, iat, jti, ath, and the server's nonce when it asks for
/// one). Redirects are followed by this class only, within the same origin; plain http only for loopback when the options
/// allow it; bodies are bounded before they are read. The client secret stays in memory for token renewals only, and no
/// message ever repeats it or the token.
/// </summary>
internal sealed class SolidSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _handler;
    private readonly ECDsa _dpopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _jwk;
    private readonly Dictionary<string, string> _nonces = new(StringComparer.Ordinal);
    private string? _accessToken;
    private DateTimeOffset _expires;
    private Func<CancellationToken, Task<(string Token, DateTimeOffset Expires)>>? _renew;

    internal SolidOptions Options { get; }
    /// <summary>The WebID this session acts as, as its token names it; null when anonymous.</summary>
    internal string? WebId { get; private set; }

    // A handler is given only by tests; whatever it is, this class follows redirects itself (FED-review: an injected client
    // that followed them would skip the same-origin rule, so one that would is refused).
    private SolidSession(HttpMessageHandler? handler, SolidOptions options)
    {
        Options = options;
        _handler = handler;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
            disposeHandler: handler is null) { Timeout = options.Timeout };
        var q = _dpopKey.ExportParameters(false).Q;
        _jwk = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.Encode(q.X!)}\",\"y\":\"{Base64Url.Encode(q.Y!)}\"}}";
    }

    internal static SolidSession Anonymous(SolidOptions? options = null, HttpMessageHandler? handler = null) => new(Checked(handler), options ?? new SolidOptions());

    /// <summary>An anonymous session with the same options and connection as this one: what a probe as anyone uses.</summary>
    internal SolidSession AnonymousTwin() => new(_handler, Options);

    private static HttpMessageHandler? Checked(HttpMessageHandler? handler)
    {
        for (var inner = handler; inner is not null; inner = (inner as DelegatingHandler)?.InnerHandler)
            if (inner is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
                throw new ArgumentException("The handler follows redirects itself, past the same-origin rule; turn AllowAutoRedirect off.", nameof(handler));
        return handler;
    }

    /// <summary>Logs in as <paramref name="webId"/> with client credentials registered for it at its OIDC issuer.</summary>
    internal static async Task<SolidSession> LoginAsync(string webId, string clientId, string clientSecret, SolidOptions? options = null,
        HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        if (WebIdGrammar.Problem(webId, "The WebID") is { } problem) throw new InvalidDataException(problem);
        var session = new SolidSession(Checked(handler), options ?? new SolidOptions());
        try
        {
            var profile = await session.ProfileAsync(webId, cancellationToken).ConfigureAwait(false);
            if (profile.Issuers.Count == 0) throw new InvalidDataException($"{webId} names no solid:oidcIssuer.");
            var issuer = profile.Issuers[0];
            session.CheckUrl(issuer);
            var discovery = await session.SendAsync(HttpMethod.Get, issuer.TrimEnd('/') + "/.well-known/openid-configuration", accept: "application/json",
                limit: session.Options.MaximumDocumentBytes, anonymous: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!discovery.IsSuccess) throw new InvalidDataException($"The issuer {issuer} gave {(int)discovery.Status} for its configuration.");
            using var configuration = Json(discovery.Body, $"The configuration at {issuer}");
            var root = configuration.RootElement;
            // The issuer that answers must be the issuer the profile names (OpenID Connect Discovery §4.3).
            if (Str(root, "issuer")?.TrimEnd('/') != issuer.TrimEnd('/'))
                throw new InvalidDataException($"The configuration at {issuer} names another issuer.");
            var tokenEndpoint = Str(root, "token_endpoint");
            if (tokenEndpoint is null) throw new InvalidDataException("The issuer names no token endpoint.");
            session.CheckUrl(tokenEndpoint);
            if (Origin(tokenEndpoint) != Origin(issuer)) throw new InvalidDataException("The token endpoint is on another origin than the issuer.");
            session._renew = ct => session.TokenAsync(tokenEndpoint, clientId, clientSecret, webId, ct);
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

    private async Task<(string Token, DateTimeOffset Expires)> TokenAsync(string tokenEndpoint, string clientId, string clientSecret, string webId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", "webid")]),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(clientSecret))));
            request.Headers.Add("DPoP", Proof("POST", tokenEndpoint, null, Nonce(tokenEndpoint)));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await Bounded(response, Options.MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
            // RFC 9449 §8: a server that wants a nonce says so once; the request is made again with it.
            if (attempt == 0 && response.StatusCode == HttpStatusCode.BadRequest && RememberNonce(tokenEndpoint, response) && Encoding.UTF8.GetString(body).Contains("use_dpop_nonce", StringComparison.Ordinal))
                continue;
            // Never echo the response: a token endpoint's error may repeat what it was sent.
            if (!response.IsSuccessStatusCode) throw new InvalidDataException($"The token endpoint refused the client credentials ({(int)response.StatusCode}).");
            RememberNonce(tokenEndpoint, response);
            using var json = Json(body, "The token endpoint's answer");
            var root = json.RootElement;
            if (!string.Equals(Str(root, "token_type"), "DPoP", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The token endpoint did not issue a DPoP-bound token.");
            var token = Str(root, "access_token");
            if (string.IsNullOrEmpty(token)) throw new InvalidDataException("The token endpoint issued no token.");
            // The token must be for the WebID this session claims (FED-review M2): credentials of another account, given with
            // a reader's WebID, would otherwise make a probe vouch for access that does not hold.
            if (TokenWebId(token) != webId) throw new InvalidDataException($"The token the issuer gave is not for {webId}; the client credentials belong to another WebID.");
            var lifetime = root.TryGetProperty("expires_in", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt32(out var s) ? s : 300;
            return (token, DateTimeOffset.UtcNow.AddSeconds(lifetime));
        }
    }

    // What a server answers is read as JSON or refused as such, never a crash (FED-review round 2).
    private static JsonDocument Json(byte[] body, string what)
    {
        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw new InvalidDataException($"{what} is not a JSON object.");
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The <c>webid</c> claim of a Solid-OIDC access token (a JWT); null when it is not one or names none.</summary>
    internal static string? TokenWebId(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || Base64Url.Decode(parts[1]) is not { } payload) return null;
        try
        {
            using var claims = JsonDocument.Parse(payload);
            return claims.RootElement.ValueKind == JsonValueKind.Object && claims.RootElement.TryGetProperty("webid", out var webId)
                && webId.ValueKind == JsonValueKind.String ? webId.GetString() : null;
        }
        catch (JsonException) { return null; }
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
    internal async Task<IReadOnlyList<RdfTriple>?> TurtleAsync(string url, bool anonymous = false, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, url, accept: "text/turtle", limit: Options.MaximumDocumentBytes, anonymous: anonymous,
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
    /// One request: authenticated with a fresh DPoP proof unless anonymous (or the session is); same-origin redirects only (at
    /// most three); the server's DPoP nonce honoured once; the body read to at most <paramref name="limit"/> bytes, or refused.
    /// </summary>
    internal async Task<SolidResponse> SendAsync(HttpMethod method, string url, byte[]? body = null, string? contentType = null, string? accept = null,
        long limit = 1 << 20, bool anonymous = false, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        CheckUrl(url);
        var origin = Origin(url);
        var nonceRetried = false;
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
            var authenticated = !anonymous && _renew is not null;
            if (authenticated)
            {
                if (_expires - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(30)) (_accessToken, _expires) = await _renew!(cancellationToken).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", _accessToken);
                request.Headers.Add("DPoP", Proof(method.Method, url, _accessToken, Nonce(url)));
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            // RFC 9449 §9: a resource server that wants a nonce answers 401 with it; the request is made once more.
            if (authenticated && !nonceRetried && response.StatusCode == HttpStatusCode.Unauthorized && RememberNonce(url, response)
                && response.Headers.WwwAuthenticate.Any(h => h.Parameter?.Contains("use_dpop_nonce", StringComparison.Ordinal) == true))
            {
                nonceRetried = true;
                hop--;
                continue;
            }
            if (authenticated) RememberNonce(url, response);
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

    private string? Nonce(string url) => _nonces.TryGetValue(Origin(url), out var nonce) ? nonce : null;

    private bool RememberNonce(string url, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("DPoP-Nonce", out var values) || values.FirstOrDefault() is not { Length: > 0 and <= 512 } nonce) return false;
        _nonces[Origin(url)] = nonce;
        return true;
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
    internal string Proof(string method, string url, string? accessToken, string? nonce = null)
    {
        var htu = url.Split('#')[0].Split('?')[0];
        var header = $"{{\"alg\":\"ES256\",\"jwk\":{_jwk},\"typ\":\"dpop+jwt\"}}";
        var claims = new StringBuilder("{");
        if (accessToken is not null) claims.Append("\"ath\":\"").Append(Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)))).Append("\",");
        claims.Append("\"htm\":\"").Append(method).Append("\",\"htu\":").Append(JsonSerializer.Serialize(htu))
            .Append(",\"iat\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Append(",\"jti\":\"").Append(Guid.NewGuid().ToString("N")).Append('"');
        if (nonce is not null) claims.Append(",\"nonce\":").Append(JsonSerializer.Serialize(nonce));
        claims.Append('}');
        var signing = Base64Url.Encode(Encoding.UTF8.GetBytes(header)) + "." + Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToString()));
        var signature = _dpopKey.SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signing + "." + Base64Url.Encode(signature);
    }

    /// <summary>
    /// https only, or http to a loopback host when the options allow it; no user information; a host name, never an IP address
    /// (FED-review round 2 M-B: an address taken from a Web document reaches whatever network this machine is on), except
    /// loopback when the options allow a local server.
    /// </summary>
    internal void CheckUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && Options.AllowLoopbackHttp))
            throw new InvalidDataException($"{url} is not an https URL{(Options.AllowLoopbackHttp ? " (or http to this machine)" : "")}; refused.");
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && !(uri.IsLoopback && Options.AllowLoopbackHttp))
            throw new InvalidDataException($"{url} names an IP address, not a host name; refused.");
    }

    internal static string Origin(string url)
    {
        var uri = new Uri(url);
        return uri.Scheme + "://" + uri.Authority;
    }

    public void Dispose()
    {
        _dpopKey.Dispose();
        _http.Dispose();
    }
}
