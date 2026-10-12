namespace AgentMemory.McpHost.Coding;

/// <summary>Local coding-mode middleware; install after routing and before HTTP/MCP handlers.</summary>
internal sealed class CodingRequestAuthorization(RequestDelegate next, CodingProjectAccess access,
    CodingServiceOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var listener = new Uri(options.Url);
        // Native clients only. Browser origins and DNS-rebinding hostnames are not local authorization.
        if (context.Request.Headers.ContainsKey("Origin") ||
            context.Request.Host.Host != listener.Host ||
            (context.Request.Host.Port ?? 80) != listener.Port)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
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
