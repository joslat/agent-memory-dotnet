using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// Result of <c>POST /v1/entities</c> -- confirmed live (Phase 10j) to have THREE genuinely different response
/// shapes depending on NAMS's own fuzzy entity-resolution outcome, not the single shape the pinned OpenAPI
/// snapshot's <c>EntityResponse</c> definition documents:
///
/// - <c>"created"</c> -- a genuinely new entity: <see cref="Name"/>/<see cref="Type"/>/<see cref="Description"/>
///   populated, <see cref="DuplicateOf"/>/<see cref="MergedInto"/>/<see cref="Confidence"/> all null.
/// - <c>"review_pending"</c> -- flagged as a probable (not certain) duplicate: same fields as <c>"created"</c>
///   plus <see cref="DuplicateOf"/> naming the entity it might duplicate.
/// - <c>"merged"</c> -- auto-merged into an existing entity with high confidence: a COMPLETELY different,
///   minimal shape -- <see cref="Name"/>/<see cref="Type"/>/<see cref="Description"/>/<see cref="DuplicateOf"/>
///   are all absent (null here); only <see cref="Id"/>/<see cref="Resolution"/>/<see cref="MergedInto"/>/
///   <see cref="Confidence"/> are present.
///
/// This was discovered the hard way: a live test using a name that happened to collide (by repeated reuse of
/// the same human-readable prefix across many probes in the same dev workspace) with an earlier probe entity
/// got auto-merged, and the original design (deserializing directly into <see cref="NamsEntity"/>) silently
/// returned null Name/Type/Description instead of failing loudly -- caught only because the live test asserted
/// on the created name, not because of any thrown exception.
///
/// Also confirmed live: NAMS's own wire casing is inconsistent between these fields -- <c>ontologyVersionId</c>/
/// <c>systemAdded</c>/<c>validationMode</c> (the "success" fields) are camelCase, while <c>duplicate_of</c>/
/// <c>merged_into</c> (the "duplicate-detection" fields) are snake_case. This is a real inconsistency in NAMS's
/// own API, not a client-side bug.
/// </summary>
internal sealed record NamsCreateEntityResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("resolution")] string Resolution,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("duplicate_of")] string? DuplicateOf,
    [property: JsonPropertyName("merged_into")] string? MergedInto,
    [property: JsonPropertyName("confidence")] double? Confidence);
