using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Result of <c>POST /v1/graph/expand</c> -- the 1-hop neighborhood of a node plus the resolved
/// relationships among the resulting node set.</summary>
internal sealed record NamsGraphExpansion(
    [property: JsonPropertyName("nodes")] IReadOnlyList<NamsExpandNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<NamsGraphEdge> Edges,
    [property: JsonPropertyName("truncated")] NamsExpandTruncation? Truncated);
