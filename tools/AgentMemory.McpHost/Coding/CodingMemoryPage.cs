namespace AgentMemory.McpHost.Coding;

internal sealed class CodingBrowserEndpoint
{
    public static CodingBrowserEndpoint Asset { get; } = new();
    public static CodingBrowserEndpoint Api { get; } = new();
    private CodingBrowserEndpoint() { }
}

/// <summary>Fixed embedded assets only; never serves repository files or embeds credentials or memories.</summary>
internal static class CodingMemoryPage
{
    public static void Map(WebApplication app)
    {
        if (!app.Services.GetRequiredService<CodingServiceOptions>().EnableBrowserUi) return;
        MapAsset(app, "ui", "index.html", "text/html");
        MapAsset(app, "ui/app.js", "app.js", "text/javascript");
        MapAsset(app, "ui/style.css", "style.css", "text/css");
    }

    private static void MapAsset(WebApplication app, string route, string file, string contentType)
    {
        using var stream = typeof(CodingMemoryPage).Assembly.GetManifestResourceStream(
            $"AgentMemory.McpHost.Coding.Page.{file}") ?? throw new InvalidOperationException("Missing memory page asset.");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        app.MapGet($"/projects/{{project}}/{route}", (HttpContext context) =>
        {
            if (context.Request.Path.Value?.EndsWith('/') == true)
                return Results.Redirect(context.Request.Path.Value.TrimEnd('/'));
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Text(content, contentType, System.Text.Encoding.UTF8);
        }).WithMetadata(CodingBrowserEndpoint.Asset);
    }
}
