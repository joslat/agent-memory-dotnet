using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// A single node returned by <c>POST /v1/graph/expand</c> -- confirmed live (Phase 10e) to be a genuinely
/// different shape from <see cref="NamsEntityGraph"/>'s flat <see cref="NamsEntity"/> nodes: expand can surface
/// non-Entity nodes (a <c>Message</c> node was observed live), so nodes here are generic graph nodes with
/// <see cref="Labels"/> and a heterogeneous <see cref="Properties"/> bag rather than fixed entity fields.
/// </summary>
internal sealed record NamsExpandNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, JsonElement> Properties);
