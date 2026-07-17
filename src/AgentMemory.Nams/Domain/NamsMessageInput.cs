using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>A message to submit via <c>POST /v1/conversations/{id}/messages/bulk</c> -- matches
/// <c>handlers.addMessageRequest</c> in the pinned OpenAPI snapshot. Doubles as the wire shape serialized into the
/// bulk request (its wire shape is identical to the caller-facing domain input).</summary>
internal sealed record NamsMessageInput(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("role")] string Role);
