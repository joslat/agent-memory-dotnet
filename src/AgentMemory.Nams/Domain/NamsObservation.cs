using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Mid-level context summary -- matches <c>handlers.Observation</c> in the pinned OpenAPI snapshot.</summary>
internal sealed record NamsObservation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conversationId")] string? ConversationId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("sourceMsgIds")] IReadOnlyList<string>? SourceMessageIds);
