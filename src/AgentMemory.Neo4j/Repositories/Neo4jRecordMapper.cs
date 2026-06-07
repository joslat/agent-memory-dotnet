using System.Text.Json;

namespace AgentMemory.Neo4j.Repositories;

/// <summary>
/// Shared (de)serialization of free-form <c>metadata</c>/<c>attributes</c> dictionaries to and from
/// the JSON strings persisted on Neo4j nodes/relationships. Centralises the round-trip that was
/// previously copy-pasted into every repository.
/// </summary>
internal static class Neo4jRecordMapper
{
    /// <summary>Serializes a metadata dictionary to JSON (an empty dictionary becomes <c>"{}"</c>).</summary>
    public static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    /// <summary>Deserializes a persisted JSON string back to a metadata dictionary (null/blank → empty).</summary>
    public static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}
