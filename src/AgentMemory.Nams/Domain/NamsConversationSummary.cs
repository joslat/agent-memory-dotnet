using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// A conversation list entry -- result of <c>GET /v1/conversations</c>. Confirmed live (Phase 10e spike) to be a
/// distinct shape from <see cref="NamsConversation"/> (the <c>POST /v1/conversations</c> create-response): list
/// entries carry <see cref="Title"/>, <see cref="FirstMessageSnippet"/>, <see cref="MessageCount"/>,
/// <see cref="CreatedAt"/>/<see cref="UpdatedAt"/> but no <c>workspaceId</c>.
/// </summary>
internal sealed record NamsConversationSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("firstMessageSnippet")] string? FirstMessageSnippet,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt);
