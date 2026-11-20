using System.Text.Json.Serialization;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Generated serializer metadata for the canonical fact key (a string array), used where reflection-based serialization
/// is switched off (the native library). Default options, like the reflection call it stands in for, so the key's
/// bytes are the same either way.
/// </summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class FactKeyJsonContext : JsonSerializerContext
{
}
