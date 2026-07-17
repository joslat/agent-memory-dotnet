using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Highest-level compressed insight -- matches <c>handlers.Reflection</c> in the pinned OpenAPI snapshot.</summary>
internal sealed record NamsReflection(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conversationId")] string? ConversationId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("sourceObsIds")] IReadOnlyList<string>? SourceObservationIds,
    [property: JsonPropertyName("supersededById")] string? SupersededById);
