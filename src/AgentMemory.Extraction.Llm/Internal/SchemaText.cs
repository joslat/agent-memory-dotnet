using System.Text.Json;

namespace AgentMemory.Extraction.Llm.Internal;

/// <summary>
/// Parses a fixed JSON schema text into a standalone element. Same element as
/// <c>JsonSerializer.Deserialize&lt;JsonElement&gt;</c>, without the serializer's reflection path, so a host with
/// reflection-based JSON off (the native library) builds the reply formats too.
/// </summary>
internal static class SchemaText
{
    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
