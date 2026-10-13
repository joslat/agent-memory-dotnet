using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace AgentMemory.McpHost.Coding;

internal static class CodingTransport
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer().WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((request, _) =>
            {
                var identity = request.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!.Features.Get<CodingIdentity>()
                    ?? throw new UnauthorizedAccessException();
                var options = request.Services!.GetRequiredService<CodingServiceOptions>();
                var definitions = CodingToolDefinitions.For(options.WriteJournal is not null, options.RecallDiagnostics is not null);
                return ValueTask.FromResult(new ListToolsResult
                {
                    Tools = identity.CanWrite ? definitions.ToArray() :
                        definitions.Where(t => t.Name is not ("remember" or "forget" or "recall_feedback")).ToArray(),
                });
            })
            .WithCallToolHandler(async (request, ct) =>
            {
                var provider = request.Services!;
                var context = provider.GetRequiredService<IHttpContextAccessor>().HttpContext!;
                var identity = context.Features.Get<CodingIdentity>() ?? throw new UnauthorizedAccessException();
                var args = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
                var result = await provider.GetRequiredService<CodingApi>()
                    .ExecuteAsync(identity, request.Params?.Name ?? "", args, ct).ConfigureAwait(false);
                return new CallToolResult
                {
                    IsError = result.StatusCode >= 400,
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result.Body, CodingApi.Json) }],
                };
            });
    }

    public static void Map(WebApplication app)
    {
        app.UseRouting();
        var options = app.Services.GetRequiredService<CodingServiceOptions>();
        if (options.Remote is not null) CodingRemoteAuthentication.Map(app, options);
        else app.UseMiddleware<CodingRequestAuthorization>();
        app.UseMiddleware<CodingBodyLimit>();
        if (options.Remote is null) CodingMemoryPage.Map(app);
        const string mcpPath = "/projects/{project}/mcp";
        // Stateless transport has no GET stream or DELETE session. Keep the project route
        // available to authorization so valid credentials do not become an OAuth challenge.
        app.MapMethods(mcpPath, [HttpMethods.Get, HttpMethods.Delete], (HttpContext context) =>
        {
            context.Response.Headers.Allow = HttpMethods.Post;
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });
        app.MapMcp(mcpPath);
        if (options.Remote is not null) return; // Remote clients use the scoped MCP contract only.
        app.MapPost("/projects/{project}/{operation}", async (HttpContext context, CodingApi api, string operation) =>
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body,
                    new JsonDocumentOptions { MaxDepth = 16 }, context.RequestAborted).ConfigureAwait(false);
                var identity = context.Features.Get<CodingIdentity>() ?? throw new UnauthorizedAccessException();
                var result = await api.ExecuteAsync(identity, operation, body.RootElement, context.RequestAborted).ConfigureAwait(false);
                return Results.Json(result.Body, CodingApi.Json, statusCode: result.StatusCode);
            }
            catch (JsonException) { return Results.Json(new { error = "invalid_input" }, statusCode: 400); }
        }).WithMetadata(CodingBrowserEndpoint.Api);
    }
}
