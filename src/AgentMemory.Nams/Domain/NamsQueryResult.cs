using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// Result of <c>POST /v1/query</c> -- a caller-supplied, server-enforced-read-only Cypher query (confirmed
/// live, Phase 10e: a real write attempt was rejected with HTTP 400). Rows are per-record dictionaries keyed
/// by column name with heterogeneous values, modeled as <see cref="JsonElement"/> -- the same untyped-payload
/// treatment already used for <c>NamsExpandNode.Properties</c> (Phase 10g) and <c>NamsEntityProvenance</c>
/// (Phase 10h). <see cref="Stats"/> is likewise untyped per the pinned schema (a fixed set of int counters was
/// observed live, but modeling it generically avoids brittleness if NAMS adds/removes fields).
/// </summary>
internal sealed record NamsQueryResult(
    [property: JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Rows,
    [property: JsonPropertyName("stats")] IReadOnlyDictionary<string, JsonElement>? Stats);
