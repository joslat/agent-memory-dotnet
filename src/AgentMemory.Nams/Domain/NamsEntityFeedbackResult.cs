using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Result of <c>PUT /v1/entities/{id}/feedback</c> -- matches <c>handlers.UpdateSchemaResponse</c> in the
/// pinned OpenAPI snapshot, confirmed live (Phase 10e/10g).</summary>
internal sealed record NamsEntityFeedbackResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("updated")] bool Updated);
