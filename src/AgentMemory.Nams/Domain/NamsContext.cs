using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Result of <c>GET /v1/conversations/{id}/context</c> -- matches <c>handlers.ContextResponse</c> in the
/// pinned OpenAPI snapshot. Three tiers: reflections (highest-level), observations (mid-level), recent messages
/// (raw).</summary>
internal sealed record NamsContext(
    [property: JsonPropertyName("reflections")] IReadOnlyList<NamsReflection> Reflections,
    [property: JsonPropertyName("observations")] IReadOnlyList<NamsObservation> Observations,
    [property: JsonPropertyName("recentMessages")] IReadOnlyList<NamsMessage> RecentMessages);
