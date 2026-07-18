using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// A graph relationship edge -- shared shape between <c>GET /v1/entities/graph</c> and
/// <c>POST /v1/graph/expand</c> (confirmed identical live, Phase 10e/10g). <see cref="Id"/> is a compound
/// <c>"sourceId|TYPE|targetId"</c> string, not a standalone identifier.
/// </summary>
internal sealed record NamsGraphEdge(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("legacyType")] string? LegacyType,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("predicate")] string? Predicate,
    [property: JsonPropertyName("sourceMessageCount")] int? SourceMessageCount);
