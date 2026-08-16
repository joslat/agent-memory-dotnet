using System.Text.Json.Serialization;

namespace Spike0.Host;

/// <summary>
/// Draft <c>am-wire/1</c> shapes, as far as Spike 0 needs them.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are a draft and are meant to be thrown away.</b> The real contract package needs the
/// response shapes Wave C and 31.1 are still landing — projection blocks, delta, certificates — and
/// building it before those exist means shipping the wire twice. What Spike 0 answers is narrower and
/// prior: <i>can a wire carry a .NET recall result at all, such that a non-.NET client reconstructs the
/// same answer?</i> If it cannot, that finding ends the spike cheaply and no contract gets written.
/// </para>
/// <para>
/// Closed shapes with explicit names, because the whole question is whether the JSON alone is
/// sufficient. Anything the DTO omits is, by construction, something a Python client cannot see.
/// </para>
/// </remarks>
internal sealed record WireRecallRequest
{
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "spike";

    /// <summary>The owner this recall is scoped to. Null recalls unscoped.</summary>
    [JsonPropertyName("userId")] public string? UserId { get; init; }

    [JsonPropertyName("query")] public string Query { get; init; } = string.Empty;

    /// <summary>Per-section caps. Absent means the engine's configured defaults.</summary>
    [JsonPropertyName("maxFacts")] public int? MaxFacts { get; init; }

    [JsonPropertyName("maxEntities")] public int? MaxEntities { get; init; }

    [JsonPropertyName("maxPreferences")] public int? MaxPreferences { get; init; }

    [JsonPropertyName("minSimilarityScore")] public double? MinSimilarityScore { get; init; }

    /// <summary>
    /// Valid-time clock for point-in-time recall — "what was true in the world at this instant".
    /// </summary>
    /// <remarks>
    /// The reason this endpoint exists in a days-long spike at all. Bitemporal recall is the one
    /// capability no other store on the target list has, so if the wire cannot express it the wire is
    /// not worth building.
    /// </remarks>
    [JsonPropertyName("asOf")] public DateTimeOffset? AsOf { get; init; }

    /// <summary>
    /// Transaction-time clock — "as the system had recorded it at this instant". Defaults to
    /// <see cref="AsOf"/> when omitted, which is single-clock recall.
    /// </summary>
    [JsonPropertyName("systemAsOf")] public DateTimeOffset? SystemAsOf { get; init; }
}

/// <summary>One recalled fact on the wire.</summary>
internal sealed record WireFact
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("subject")] public required string Subject { get; init; }
    [JsonPropertyName("predicate")] public required string Predicate { get; init; }
    [JsonPropertyName("object")] public required string Object { get; init; }
    [JsonPropertyName("confidence")] public required double Confidence { get; init; }

    /// <summary>Real-world validity window. Null means unbounded on that side.</summary>
    [JsonPropertyName("validFrom")] public DateTimeOffset? ValidFrom { get; init; }

    [JsonPropertyName("validUntil")] public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>
    /// The owner, carried explicitly rather than left implicit in the request scope.
    /// </summary>
    /// <remarks>
    /// A client that cannot see which owner a fact belongs to cannot verify isolation held — and
    /// "isolation held" is one of the five fixtures. Omitting it would make the isolation case
    /// unfalsifiable from the wire, which is the same as not testing it.
    /// </remarks>
    [JsonPropertyName("ownerId")] public string? OwnerId { get; init; }
}

internal sealed record WireEntity
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("ownerId")] public string? OwnerId { get; init; }
}

internal sealed record WirePreference
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("ownerId")] public string? OwnerId { get; init; }
}

internal sealed record WireRecallResponse
{
    [JsonPropertyName("totalItemsRetrieved")] public required int TotalItemsRetrieved { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("facts")] public required IReadOnlyList<WireFact> Facts { get; init; }
    [JsonPropertyName("entities")] public required IReadOnlyList<WireEntity> Entities { get; init; }
    [JsonPropertyName("preferences")] public required IReadOnlyList<WirePreference> Preferences { get; init; }
}

/// <summary>
/// The canonical projection both paths are compared on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a third shape rather than comparing the two directly.</b> The wire response and the in-process
/// domain result have different structures by design, so a byte-compare between them would only ever
/// say "these are different types". The question Spike 0 asks is narrower and more useful: given only
/// the wire JSON, can a non-.NET client reconstruct the same answer the .NET caller got?
/// </para>
/// <para>
/// So the parity script builds this projection <b>in Python, from the wire JSON</b>, and the host builds
/// the same projection <b>in C#, from the domain object</b>. A field the DTO fails to carry is a field
/// Python cannot reconstruct, and the compare fails — which is exactly the failure mode worth finding
/// before a contract package is written.
/// </para>
/// </remarks>
internal sealed record CanonicalRecall
{
    [JsonPropertyName("totalItemsRetrieved")] public required int TotalItemsRetrieved { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }

    /// <summary>Sorted, so ordering differences between the two paths are not mistaken for content differences.</summary>
    [JsonPropertyName("facts")] public required IReadOnlyList<string> Facts { get; init; }

    [JsonPropertyName("entities")] public required IReadOnlyList<string> Entities { get; init; }
    [JsonPropertyName("preferences")] public required IReadOnlyList<string> Preferences { get; init; }
}
