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
    /// <remarks>
    /// A host that switches reflection-based serialization off (the Native AOT library) writes through generated
    /// metadata with the same options, the same bytes for the value types it lists, and refuses any other value type;
    /// every other host runs the call it always has.
    /// </remarks>
    public static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}"
            : JsonSerializer.IsReflectionEnabledByDefault
                ? JsonSerializer.Serialize(metadata)
                : JsonSerializer.Serialize(metadata, Neo4jMetadataJsonContext.Default.IReadOnlyDictionaryStringObject);

    /// <summary>Deserializes a persisted JSON string back to a metadata dictionary (null/blank → empty).</summary>
    /// <remarks>
    /// A host that switches reflection-based serialization off (the Native AOT speech library) reads through generated
    /// metadata with the same options and converters; every other host runs the call it always has. The switch is a
    /// runtime feature flag, so a native build removes the reflection branch altogether.
    /// </remarks>
    public static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object>();

        var metadata = JsonSerializer.IsReflectionEnabledByDefault
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(json)
            : JsonSerializer.Deserialize(json, Neo4jMetadataJsonContext.Default.DictionaryStringObject);
        return metadata ?? new Dictionary<string, object>();
    }
}
