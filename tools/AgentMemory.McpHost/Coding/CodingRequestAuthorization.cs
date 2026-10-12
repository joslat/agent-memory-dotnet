namespace AgentMemory.McpHost.Coding;

/// <summary>Local coding-mode middleware; install after routing and before HTTP/MCP handlers.</summary>
internal sealed class CodingRequestAuthorization(RequestDelegate next, CodingProjectAccess access,
    CodingServiceOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var listener = new Uri(options.Url);
        var browser = context.GetEndpoint()?.Metadata.GetMetadata<CodingBrowserEndpoint>();
        var origins = context.Request.Headers.Origin;
        var allowedOrigin = options.EnableBrowserUi && browser is not null && origins.Count == 1 &&
            origins[0] == listener.GetLeftPart(UriPartial.Authority);
        if ((context.Request.Headers.ContainsKey("Origin") && !allowedOrigin) ||
            context.Request.Host.Host != listener.Host ||
            (context.Request.Host.Port ?? 80) != listener.Port)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Only explicitly mapped, data-free assets bypass bearer authorization. Never an API or MCP route.
        if (options.EnableBrowserUi && browser == CodingBrowserEndpoint.Asset)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var project = context.Request.RouteValues["project"] as string;
        var headers = context.Request.Headers.Authorization;
        var identity = project is not null && headers.Count == 1
            ? access.Authorize(project, headers[0] ?? string.Empty)
            : null;
        if (identity is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Features.Set(identity);
        try { await next(context).ConfigureAwait(false); }
        finally { context.Features.Set<CodingIdentity>(null); }
    }
}
