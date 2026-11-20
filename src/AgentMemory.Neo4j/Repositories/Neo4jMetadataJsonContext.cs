using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Neo4j.Repositories;

/// <summary>
/// Generated serializer metadata for persisted metadata dictionaries in a host that switches reflection-based
/// serialization off (the Native AOT library). It uses the default options, exactly as the reflection calls in
/// <see cref="Neo4jRecordMapper"/> do. Reading: non-null values as <see cref="JsonElement"/>, JSON null as null, a
/// repeated key's last value. Writing: the value types listed here, which cover what hosts and the library put into
/// metadata (text, numbers, booleans, times, identifiers, values read back from the store, nested dictionaries and
/// lists); any other value type is refused with <see cref="NotSupportedException"/> rather than written differently.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, object>))]
[JsonSerializable(typeof(IDictionary<string, object>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(bool[]))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<Dictionary<string, object>>))]
internal sealed partial class Neo4jMetadataJsonContext : JsonSerializerContext
{
}
