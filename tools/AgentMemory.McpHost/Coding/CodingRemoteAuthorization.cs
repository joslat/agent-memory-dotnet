using Microsoft.AspNetCore.Authentication;

namespace AgentMemory.McpHost.Coding;

/// <summary>Reject untrusted transport and unbounded credentials before OAuth discovery or signature work.</summary>
internal sealed class CodingRemoteBoundary(RequestDelegate next, CodingServiceOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var expected = new Uri(options.Url);
        if (!context.Request.IsHttps || context.Request.Host.Host != expected.Host ||
            (context.Request.Host.Port ?? 443) != expected.Port || context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = 403;
            return;
        }
        var project = context.Request.RouteValues["project"] as string;
        if (project is null || !options.Projects.ContainsKey(project) || context.GetEndpoint() is null)
        {
            context.Response.StatusCode = 404;
            return;
        }
        var headers = context.Request.Headers.Authorization;
        if (headers.Count > 1 || (headers.Count == 1 && headers[0]!.Length > 8192))
        {
            context.Response.StatusCode = 400;
            return;
        }
        await next(context).ConfigureAwait(false);
    }
}

internal sealed class CodingRemoteAuthorization(RequestDelegate next, CodingServiceOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<CodingRemoteMetadata>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        var project = (string)context.Request.RouteValues["project"]!;
        var requestedScopes = options.Remote!.AllowWrites
            ? CodingRemoteAuthentication.ReadScope + " " + CodingRemoteAuthentication.WriteScope : CodingRemoteAuthentication.ReadScope;
        var challenge = $"Bearer resource_metadata=\"{CodingRemoteOptions.Metadata(options, project)}\", scope=\"{requestedScopes}\"";
        var result = await context.AuthenticateAsync(CodingRemoteAuthentication.Scheme).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = challenge;
            return;
        }
        var scopeClaims = result.Principal!.FindAll("scope").ToArray();
        var scopes = scopeClaims.Length == 1 ? scopeClaims[0].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        if (!scopes.Contains(CodingRemoteAuthentication.ReadScope, StringComparer.Ordinal))
        {
            context.Response.StatusCode = 403;
            context.Response.Headers.WWWAuthenticate = challenge + ", error=\"insufficient_scope\"";
            return;
        }
        context.Features.Set(new CodingIdentity(options.OwnerId, project)
        {
            CanWrite = options.Remote.AllowWrites && scopes.Contains(CodingRemoteAuthentication.WriteScope, StringComparer.Ordinal),
        });
        try { await next(context).ConfigureAwait(false); }
        finally { context.Features.Set<CodingIdentity>(null); }
    }
}
