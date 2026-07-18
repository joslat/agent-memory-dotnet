using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Truncation metadata on a <c>POST /v1/graph/expand</c> response -- confirmed live (Phase 10e).</summary>
internal sealed record NamsExpandTruncation(
    [property: JsonPropertyName("nodeId")] string? NodeId,
    [property: JsonPropertyName("shown")] int Shown,
    [property: JsonPropertyName("total")] int Total);
