using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Result element of <c>POST /v1/entities/search</c> -- matches <c>handlers.Entity</c> in the pinned
/// OpenAPI snapshot.</summary>
internal sealed record NamsEntity(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("sourceStage")] string? SourceStage,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt);
