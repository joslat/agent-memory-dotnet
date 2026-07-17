using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>A message as returned by NAMS -- covers both <c>handlers.Message</c> (context's <c>recentMessages</c>,
/// which carries a relevance <see cref="Score"/> and <see cref="TokenCount"/> but no conversation id) and
/// <c>handlers.AddMessageResponse</c> (the bulk-add result, which carries <see cref="ConversationId"/> but neither
/// of those). One shape covers both rather than two near-duplicate records.</summary>
internal sealed record NamsMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("conversationId")] string? ConversationId,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("tokenCount")] int? TokenCount);
