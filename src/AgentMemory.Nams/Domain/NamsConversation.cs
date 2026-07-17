using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Result of <c>POST /v1/conversations</c> -- matches <c>handlers.CreateConversationResponse</c> in the
/// pinned OpenAPI snapshot (<c>docs/reviews/nams-openapi-snapshot-2026-07-17.json</c>).</summary>
internal sealed record NamsConversation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);
