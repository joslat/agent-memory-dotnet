using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.McpServer.Nams.Tools;

/// <summary>
/// Shared JSON serialization options for NAMS MCP tool results. A small, self-contained copy of
/// <c>AgentMemory.McpServer.Tools.ToolJsonContext</c>'s shape rather than a dependency on it -- that type is
/// internal to its own package, and this package otherwise has no reason to reference
/// <c>AgentMemory.McpServer</c> at all.
/// </summary>
internal static class NamsMcpToolJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
