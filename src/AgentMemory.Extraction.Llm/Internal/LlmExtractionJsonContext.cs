using System.Text.Json.Serialization;

namespace AgentMemory.Extraction.Llm.Internal;

/// <summary>
/// Generated metadata for the extraction reply, used where reflection-based JSON is off (the native library). Same
/// shapes and matching as the reflection path: case-insensitive names, the date converters named on the properties.
/// The parity tests read the same replies both ways.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LlmExtractionResponse))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(LlmEntityDto))]
[JsonSerializable(typeof(LlmFactDto))]
[JsonSerializable(typeof(LlmPreferenceDto))]
[JsonSerializable(typeof(LlmRelationshipDto))]
internal sealed partial class LlmExtractionJsonContext : JsonSerializerContext;
