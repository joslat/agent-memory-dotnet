using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// Result of <c>GET /v1/entities/graph</c> -- the whole workspace entity graph. Nodes are confirmed live
/// (Phase 10e) to be shaped identically to <see cref="NamsEntity"/> (id/name/type/description/confidence/
/// sourceStage/createdAt/updatedAt) -- reused directly rather than duplicating a near-identical record.
/// </summary>
internal sealed record NamsEntityGraph(
    [property: JsonPropertyName("nodes")] IReadOnlyList<NamsEntity> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<NamsGraphEdge> Edges);
