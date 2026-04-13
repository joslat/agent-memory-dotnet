using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neo4j.AgentMemory.McpServer.Tools;

/// <summary>
/// Shared JSON serialization options for MCP tool results.
/// </summary>
internal static class ToolJsonContext
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    internal static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);
}
