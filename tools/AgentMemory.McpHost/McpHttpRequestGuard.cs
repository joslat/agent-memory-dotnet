using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentMemory.McpHost;

/// <summary>
/// RVW-MCP-ORIGIN: the plain HTTP host answers only requests addressed to it. It has no authentication and is meant for
/// localhost, so a web page must not reach it through DNS rebinding (the page's own domain made to resolve to
/// 127.0.0.1, its requests then sent to this server with the page's Host and Origin). The MCP specification asks HTTP
/// servers to validate Origin for exactly this.
/// </summary>
/// <remarks>
/// A request passes when its Host names a loopback name, the listener's own host (when it listens on one host rather
/// than all of them) or a host the operator allowed (<see cref="McpHostOptions.AllowedHosts"/>), and its Origin, when
/// it has one, names one of those too. Ports are not compared: Docker may publish the server on another port. A
/// browser page served from this machine is the operator's own; a page anywhere else is refused.
/// </remarks>
internal static class McpHttpRequestGuard
{
    private static readonly string[] Loopback = ["localhost", "127.0.0.1", "::1"];

    /// <summary>The host names a request may address: loopback, the listener's own host and the operator's.</summary>
    internal static HashSet<string> AllowedHosts(McpHostOptions options)
    {
        var allowed = new HashSet<string>(Loopback, StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(options.HttpUrl, UriKind.Absolute, out var listener) && !ListensOnEveryAddress(listener.Host))
            allowed.Add(Normalize(listener.Host));
        foreach (var host in options.AllowedHosts) allowed.Add(Normalize(host));
        return allowed;
    }

    /// <summary>Whether a request with this Host and Origin is addressed to this server.</summary>
    internal static bool Allows(HostString host, string? origin, IReadOnlySet<string> allowed)
    {
        if (!host.HasValue || !allowed.Contains(Normalize(host.Host))) return false;
        if (string.IsNullOrEmpty(origin)) return true;
        // "null" (a sandboxed frame, a file) and anything unparsable name no host of ours.
        return Uri.TryCreate(origin, UriKind.Absolute, out var page) && allowed.Contains(Normalize(page.Host));
    }

    /// <summary>Refuses, with 403, every request the guard does not allow; install before the MCP endpoint.</summary>
    internal static void Use(WebApplication app, McpHostOptions options)
    {
        var allowed = AllowedHosts(options);
        app.Use(async (context, next) =>
        {
            var origins = context.Request.Headers.Origin;
            if (origins.Count > 1 || !Allows(context.Request.Host, origins.Count == 1 ? origins[0] : null, allowed))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "This server answers requests addressed to localhost or to a host allowed with --allowed-hosts.",
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }
            await next(context).ConfigureAwait(false);
        });
    }

    private static bool ListensOnEveryAddress(string host) =>
        host is "0.0.0.0" or "[::]" or "::" or "+" or "*";

    private static string Normalize(string host) => host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
}
