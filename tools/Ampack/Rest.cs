using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;

namespace Ampack;

/// <summary>
/// <c>ampack serve --http</c> (pack format v1.2 §10, PACK-serve): the packs <see cref="Serve"/> mounts, served read-only over
/// HTTP for any client in any language. The same three operations as the MCP tools, answered by the same code
/// (<see cref="PackTools"/>), so the two never drift:
/// <list type="bullet">
/// <item><c>GET /v1/meta</c>: the server and what it holds;</item>
/// <item><c>GET /v1/packs</c>: the packs (pack_list);</item>
/// <item><c>POST /v1/packs/{id}/views/{view}/ask</c> with <c>{"text"|"key", "limit"}</c>: one view (pack_ask);</item>
/// <item><c>POST /v1/walk</c> with <c>{"paths", "symbols", "question"}</c>: the walk from the work in hand (pack_walk).</item>
/// </list>
/// Loopback only unless the host says otherwise; request bodies are capped; nothing writes. Beyond this machine
/// (<c>--allow-remote</c>, review 1, M1): every request carries <c>Authorization: Bearer</c> with the token in
/// <c>AMPACK_SERVE_TOKEN</c> (at least 32 characters), packs published for named WebIDs are not served, and TLS is the job of a
/// reverse proxy in front. At most <see cref="MaximumConcurrent"/> requests are answered at once; more get 503.
/// </summary>
internal static class Rest
{
    private const int MaximumBody = 64 * 1024;
    internal const int MaximumConcurrent = 16;
    internal const string TokenVariable = "AMPACK_SERVE_TOKEN";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static int Run(PackMount mount, string url, bool allowRemote)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new UsageException("--http takes a URL such as http://localhost:5199/.");
        if (!uri.IsLoopback && !allowRemote)
            throw new UsageException($"{uri.Host} is not this machine: serving packs beyond it needs --allow-remote.");
        // Remote is what --allow-remote says, not only the address (review 3: a proxy on this machine forwarding to a loopback
        // address passed every local check). Remote: a token on every request, packs for named WebIDs never served.
        var remote = allowRemote || !uri.IsLoopback;
        var token = Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } set ? set : null;
        if (token is { Length: < 32 }) throw new UsageException($"{TokenVariable} is shorter than 32 characters.");
        if (remote && token is null)
            throw new UsageException($"Serving beyond this machine needs a bearer token: set {TokenVariable} (at least 32 characters); every request then sends Authorization: Bearer <token>. Put a TLS proxy in front.");
        // Packs published for named WebIDs need the token on loopback too (review 4): a proxy on this machine that adds no
        // forwarding header is indistinguishable from a local client, so the address alone cannot keep them local.
        if (token is null && mount.Packs.FirstOrDefault(p => p.Publication is { IsPublic: false }) is { } named)
            throw new UsageException($"{named.Id} is published for named WebIDs only: serving it over HTTP needs a bearer token, on this machine too (set {TokenVariable}).");
        if (remote && mount.Packs.FirstOrDefault(p => p.Publication is { IsPublic: false }) is { } restricted)
            throw new UsageException($"{restricted.Id} is published for named WebIDs only: it is not served beyond this machine.");
        var expected = token is null ? null : System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token));
        using var gate = new SemaphoreSlim(MaximumConcurrent);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"{uri.Scheme}://{uri.Host}:{uri.Port}/");
        listener.Start();
        Console.Error.WriteLine($"ampack serve: {mount.Packs.Count} pack(s), {mount.Memory.Count:N0} records, REST on {uri.Scheme}://{uri.Host}:{uri.Port}/v1/");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var registration = stop.Token.Register(listener.Stop);
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = listener.GetContext(); }
            catch (HttpListenerException) when (stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            // One client's broken request never ends the server (review 2): the next one is accepted.
            catch (HttpListenerException) when (listener.IsListening) { continue; }
            if (!gate.Wait(0)) { Refuse(context, 503, "The server is busy; try again."); continue; }
            // A server bound to this machine answers this machine only, whatever Host header a request names (review 2: http.sys
            // routes by the Host header, so a localhost prefix could be reached from elsewhere).
            if (!remote && (!context.Request.IsLocal || Forwarded(context.Request)))
            { gate.Release(); Refuse(context, 403, "This server answers this machine only; serving through a proxy needs --allow-remote."); continue; }
            if (expected is not null && !Authorized(context.Request, expected)) { gate.Release(); Refuse(context, 401, "A bearer token is required."); continue; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(mount, context, stop.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is HttpListenerException or OperationCanceledException or ObjectDisposedException or IOException) { }
                finally { gate.Release(); }
            });
        }
        return 0;
    }

    private static async Task HandleAsync(PackMount mount, HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var segments = request.Url!.AbsolutePath.Trim('/').Split('/');
        int status;
        string body;
        try
        {
            (status, body) = (request.HttpMethod, segments) switch
            {
                ("GET", ["v1", "meta"]) => (200, JsonSerializer.Serialize(new
                {
                    server = "ampack", version = typeof(Rest).Assembly.GetName().Version?.ToString(), format = "agentmemory.pack/1.2",
                    packs = mount.Packs.Count, records = mount.Memory.Count,
                    operations = new[] { "GET /v1/packs", "POST /v1/packs/{id}/views/{view}/ask", "POST /v1/walk" },
                }, Json)),
                ("GET", ["v1", "packs"]) => (200, PackTools.List(mount)),
                ("POST", ["v1", "packs", var pack, "views", var view, "ask"]) => await AskAsync(mount, Uri.UnescapeDataString(pack), Uri.UnescapeDataString(view),
                    await BodyAsync(request).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
                ("POST", ["v1", "walk"]) => await WalkAsync(mount, await BodyAsync(request).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
                _ => (404, Error($"No operation {request.HttpMethod} {request.Url.AbsolutePath}; GET /v1/meta lists them.")),
            };
        }
        catch (BadRequest e) { (status, body) = (400, Error(e.Message)); }
        catch (Exception e) when (e is not OperationCanceledException) { (status, body) = (500, Error("The server could not answer: " + e.GetType().Name + ".")); }
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        try { await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
        finally { context.Response.Close(); }
    }

    // A request a proxy forwarded: it came from elsewhere, whatever address the connection has. Best effort: a proxy that adds
    // none of these headers is not seen, which is why packs for named WebIDs need the token on loopback too.
    private static readonly string[] ForwardingHeaders = ["Forwarded", "Via", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP"];

    private static bool Forwarded(HttpListenerRequest request) => ForwardingHeaders.Any(h => request.Headers[h] is not null);

    // The bearer token, compared in constant time by its hash.
    private static bool Authorized(HttpListenerRequest request, byte[] expected) =>
        request.Headers["Authorization"] is { } header && header.StartsWith("Bearer ", StringComparison.Ordinal)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..])), expected);

    // Runs on the accept loop, so nothing a client does to its connection may escape it (review 2: a reset killed the server).
    private static void Refuse(HttpListenerContext context, int status, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Error(message));
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            if (status == 401) context.Response.AddHeader("WWW-Authenticate", "Bearer");
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            try { context.Response.Close(); }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private static async Task<(int, string)> AskAsync(PackMount mount, string pack, string view, JsonObject body, CancellationToken cancellationToken)
    {
        var result = await PackTools.Ask(mount, pack, view, Text(body, "text"), Text(body, "key"),
            body["limit"] is JsonValue limit && limit.TryGetValue<int>(out var n) ? n : null, cancellationToken).ConfigureAwait(false);
        return (IsError(result) ? 400 : 200, result);
    }

    private static async Task<(int, string)> WalkAsync(PackMount mount, JsonObject body, CancellationToken cancellationToken)
    {
        var result = await PackTools.Walk(mount, Strings(body, "paths"), Strings(body, "symbols"), Text(body, "question"), cancellationToken).ConfigureAwait(false);
        return (IsError(result) ? 400 : 200, result);
    }

    // The request body: a JSON object of at most 64 KiB; an empty body is an empty object.
    private static async Task<JsonObject> BodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaximumBody) throw new BadRequest($"The body is larger than {MaximumBody} bytes.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaximumBody) throw new BadRequest($"The body is larger than {MaximumBody} bytes.");
        }
        if (buffer.Length == 0) return [];
        try { return JsonNode.Parse(buffer.ToArray()) as JsonObject ?? throw new BadRequest("The body is a JSON object."); }
        catch (JsonException) { throw new BadRequest("The body is not JSON."); }
    }

    private static string? Text(JsonObject body, string name) =>
        body[name] is null ? null : body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new BadRequest($"'{name}' is a string.");

    private static string[]? Strings(JsonObject body, string name) =>
        body[name] is null ? null : body[name] is JsonArray a && a.All(x => x is JsonValue v && v.TryGetValue<string>(out _))
            ? [.. a.Select(x => x!.GetValue<string>())] : throw new BadRequest($"'{name}' is a list of strings.");

    private static bool IsError(string json) => json.StartsWith("{\"error\"", StringComparison.Ordinal);

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);

    private sealed class BadRequest(string message) : Exception(message);
}
