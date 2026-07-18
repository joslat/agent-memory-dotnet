using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// Result of <c>GET /v1/reasoning/provenance/{entityId}</c> -- the reasoning chain that influenced an entity's
/// creation. The envelope's array field is confirmed live (Phase 10e) to be named <see cref="Provenance"/>, NOT
/// "steps" as the pinned OpenAPI snapshot's schema name wrongly implied. Individual entry shape is genuinely
/// unconfirmed -- the Phase 10e probe only observed an empty array (no entity with a recorded reasoning link
/// was available), and the pinned snapshot itself only documents entries as untyped. Modeled as raw
/// <see cref="JsonElement"/> rather than guessing a concrete shape that might not match reality -- exactly the
/// mistake the pinned snapshot already made once on the envelope field name.
/// </summary>
internal sealed record NamsEntityProvenance(
    [property: JsonPropertyName("entityId")] string EntityId,
    [property: JsonPropertyName("provenance")] IReadOnlyList<JsonElement> Provenance);
