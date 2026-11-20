using System.Text.Json.Serialization;

namespace AgentMemory.Core.Memory;

/// <summary>
/// Generated serializer metadata for the embedded relation vocabulary, used where reflection-based serialization is
/// switched off (the Native AOT speech library). Case-insensitive like the reflection call it stands in for.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RelationVocabularyDocument))]
internal sealed partial class RelationVocabularyJsonContext : JsonSerializerContext
{
}
