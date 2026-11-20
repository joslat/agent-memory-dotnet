using System.Text.Json.Serialization;

namespace AgentMemory.Neo4j.Repositories;

/// <summary>
/// Generated serializer metadata for reading persisted metadata dictionaries in a host that switches reflection-based
/// serialization off (the Native AOT speech library). It uses the default options, exactly as the reflection call in
/// <see cref="Neo4jRecordMapper"/> does, so both read stored JSON through the same converters: non-null values as
/// <see cref="System.Text.Json.JsonElement"/>, JSON null as null, a repeated key's last value.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, object>))]
internal sealed partial class Neo4jMetadataJsonContext : JsonSerializerContext
{
}
