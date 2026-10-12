using System.Text.Json;

namespace AgentMemory.McpHost.Coding.Client;

internal static class CodingHookInput
{
    public static string? Text(JsonElement input, string name) => input.ValueKind == JsonValueKind.Object &&
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : null;
}
