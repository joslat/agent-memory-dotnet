using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentMemory.McpServer;

/// <summary>
/// Pins every call to <see cref="AgentMemoryMcpOptions.OwnerId"/> when it is set.
/// </summary>
/// <remarks>
/// <para>
/// Without a pin the model chooses the owner: <c>userId</c> is an ordinary tool argument, and an omitted one
/// means shared memory. A host that knows who it serves sets the owner instead, the way the NAMS host resolves
/// identity rather than reading it from the model.
/// </para>
/// <para>
/// Applied once, as request filters, rather than in each tool, so a tool added later is pinned without anyone
/// remembering to. The listed schemas lose <c>userId</c> so a model is not invited to pass one; a call that
/// still names another owner is refused rather than silently redirected, because the caller asked for
/// something this server will not do.
/// </para>
/// </remarks>
internal static class McpOwnerPin
{
    internal const string Parameter = "userId";

    internal const string Refusal =
        "This memory server is pinned to one owner and does not act for another userId. Omit userId.";

    internal static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListTools(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) => async (context, cancellationToken) =>
    {
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        if (OwnerOf(context.Services) is null) return result;
        // Copies, never the registered instances: the call filter reads the original schema to know which
        // tools take an owner.
        return new ListToolsResult
        {
            Tools = result.Tools.Select(WithoutOwner).ToList(),
            NextCursor = result.NextCursor,
            Meta = result.Meta,
        };
    };

    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> CallTool(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => (context, cancellationToken) =>
    {
        if (OwnerOf(context.Services) is not { } owner || context.Params is not { } parameters)
            return next(context, cancellationToken);
        if (NamesAnotherOwner(owner, parameters.Arguments))
            return ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Refusal }] });
        if (AcceptsOwner((context.MatchedPrimitive as McpServerTool)?.ProtocolTool.InputSchema))
            parameters.Arguments = WithOwner(owner, parameters.Arguments);
        return next(context, cancellationToken);
    };

    /// <summary>The owner a list resource reads: the pinned one, or the caller's when nothing is pinned.</summary>
    /// <exception cref="McpException">The caller named an owner other than the pinned one.</exception>
    internal static string? ForResource(AgentMemoryMcpOptions options, string? userId)
    {
        if (Pinned(options.OwnerId) is not { } owner) return userId;
        if (!string.IsNullOrEmpty(userId) && !string.Equals(userId, owner, StringComparison.Ordinal))
            throw new McpException(Refusal);
        return owner;
    }

    /// <summary>Whether the arguments name an owner other than <paramref name="owner"/>. A null or blank one names none.</summary>
    internal static bool NamesAnotherOwner(string owner, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(Parameter, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        var requested = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return !string.IsNullOrEmpty(requested) && !string.Equals(requested, owner, StringComparison.Ordinal);
    }

    /// <summary>Whether a tool's input schema declares the owner parameter.</summary>
    internal static bool AcceptsOwner(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s
        && s.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(Parameter, out _);

    /// <summary>The arguments with <c>userId</c> set to <paramref name="owner"/>; the caller's dictionary is not changed.</summary>
    internal static IDictionary<string, JsonElement> WithOwner(string owner, IDictionary<string, JsonElement>? arguments)
    {
        var pinned = arguments is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);
        using var document = JsonDocument.Parse("\"" + JsonEncodedText.Encode(owner) + "\"");
        pinned[Parameter] = document.RootElement.Clone();
        return pinned;
    }

    /// <summary>A copy of the tool whose input schema no longer declares or requires <c>userId</c>.</summary>
    internal static Tool WithoutOwner(Tool tool)
    {
        var copy = JsonSerializer.Deserialize<Tool>(JsonSerializer.SerializeToUtf8Bytes(tool, McpJsonUtilities.DefaultOptions),
            McpJsonUtilities.DefaultOptions) ?? throw new InvalidOperationException("A listed tool could not be copied.");
        if (!AcceptsOwner(tool.InputSchema) || JsonNode.Parse(tool.InputSchema.GetRawText()) is not JsonObject schema) return copy;
        (schema["properties"] as JsonObject)?.Remove(Parameter);
        if (schema["required"] is JsonArray required)
            foreach (var entry in required.Where(r => r?.GetValue<string>() == Parameter).ToList()) required.Remove(entry);
        using var document = JsonDocument.Parse(schema.ToJsonString());
        copy.InputSchema = document.RootElement.Clone();
        return copy;
    }

    private static string? OwnerOf(IServiceProvider? services) =>
        Pinned(services?.GetService<IOptions<AgentMemoryMcpOptions>>()?.Value.OwnerId);

    private static string? Pinned(string? ownerId) => string.IsNullOrWhiteSpace(ownerId) ? null : ownerId;
}
