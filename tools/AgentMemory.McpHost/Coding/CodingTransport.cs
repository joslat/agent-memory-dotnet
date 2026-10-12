using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace AgentMemory.McpHost.Coding;

internal static class CodingTransport
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer().WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = CodingToolDefinitions.All }))
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
        app.UseMiddleware<CodingRequestAuthorization>();
        app.UseMiddleware<CodingBodyLimit>();
        CodingMemoryPage.Map(app);
        app.MapMcp("/projects/{project}/mcp");
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
