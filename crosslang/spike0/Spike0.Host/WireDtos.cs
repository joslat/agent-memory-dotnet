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

// ── D2: the verbs the LangGraph BaseStore adapter maps onto ───────────────────────────────────────
//
// Spike 0's wire was read-only — enough to answer "can a wire carry a recall result". D2 needs three
// more verbs, because a store that cannot write is not a store: a typed write (put), a point-read
// (get), and the resume brief (delta). Still draft, still throwaway; the real contract waits on 31.1.

/// <summary>A typed fact write, which is what LangGraph's <c>put()</c> maps onto (D2).</summary>
/// <remarks>
/// The caller supplies the <see cref="Key"/> and it becomes the fact id, because LangGraph owns key
/// identity: a store that invented its own id would make <c>put</c>-then-<c>get</c> fail, and fail for
/// a reason the client has no way to see.
/// </remarks>
internal sealed record WireFactWrite
{
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("ownerId")] public string? OwnerId { get; init; }
    [JsonPropertyName("subject")] public required string Subject { get; init; }
    [JsonPropertyName("predicate")] public required string Predicate { get; init; }
    [JsonPropertyName("object")] public required string Object { get; init; }
    [JsonPropertyName("confidence")] public double? Confidence { get; init; }
    [JsonPropertyName("validFrom")] public DateTimeOffset? ValidFrom { get; init; }
    [JsonPropertyName("validUntil")] public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>
    /// When the system LEARNED this — the transaction clock — as distinct from when it became true in
    /// the world (<see cref="ValidFrom"/>). Defaults to now.
    /// </summary>
    /// <remarks>
    /// Exposed because a demo has to compress months into one process, and the two clocks are not
    /// interchangeable: recording everything at "now" and then asking as-of March correctly returns
    /// nothing, because in March the system knew nothing. That is right, and it makes a demo look
    /// broken — so the write carries the transaction instant explicitly rather than the read quietly
    /// ignoring one of the clocks to produce a friendlier answer.
    /// </remarks>
    [JsonPropertyName("recordedAtUtc")] public DateTimeOffset? RecordedAtUtc { get; init; }

    /// <summary>
    /// The id of a fact this one replaces. Applied as a real supersession, not a delete.
    /// </summary>
    /// <remarks>
    /// This is how an <i>update</i> is expressed. A key-value store overwrites and the old value is
    /// gone; here the loser is closed on the transaction clock, so as-of recall before this instant
    /// still returns it — which is the whole reason the history is worth keeping.
    /// </remarks>
    [JsonPropertyName("supersedes")] public string? Supersedes { get; init; }
}

/// <summary>"What changed since I was last here" (D2's resume brief).</summary>
internal sealed record WireDeltaRequest
{
    [JsonPropertyName("ownerId")] public string? OwnerId { get; init; }
    [JsonPropertyName("since")] public required DateTimeOffset Since { get; init; }
    [JsonPropertyName("maxItemsPerSection")] public int? MaxItemsPerSection { get; init; }
}

/// <summary>A replacement, carried as a pair so the client can render "was X, now Y".</summary>
internal sealed record WireSupersededPair
{
    [JsonPropertyName("old")] public required WireFact Old { get; init; }
    [JsonPropertyName("new")] public required WireFact New { get; init; }
}

internal sealed record WireDeltaResponse
{
    [JsonPropertyName("since")] public required DateTimeOffset Since { get; init; }

    /// <summary>
    /// The next checkpoint, handed back rather than left for the caller to guess.
    /// </summary>
    /// <remarks>
    /// A client that stamped its own "now" after the call would leave a gap between the server's read
    /// and its own clock, and anything written in that gap would never appear in any delta. The window
    /// is half-open on the server's clock, so echoing this value back partitions time exactly.
    /// </remarks>
    [JsonPropertyName("takenAtUtc")] public required DateTimeOffset TakenAtUtc { get; init; }

    [JsonPropertyName("newFacts")] public required IReadOnlyList<WireFact> NewFacts { get; init; }
    [JsonPropertyName("supersededPairs")] public required IReadOnlyList<WireSupersededPair> SupersededPairs { get; init; }
    [JsonPropertyName("invalidatedFacts")] public required IReadOnlyList<WireFact> InvalidatedFacts { get; init; }

    /// <summary>Buckets that hit their cap. Truncation is reported, never silent.</summary>
    [JsonPropertyName("truncatedSections")] public required IReadOnlyList<string> TruncatedSections { get; init; }
}

/// <summary>
/// One provenance row: what this memory is, what replaced it, and where it came from.
/// </summary>
/// <remarks>
/// The demo's "why do you believe that?" walk. The two supersession lists are what make an
/// <c>as_of</c> answer auditable rather than merely surprising: the closed fact is still here, still
/// readable, still pointing at the fact that replaced it. A store that overwrote has nothing to walk.
/// </remarks>
internal sealed record WireHistoryRow
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
    [JsonPropertyName("ownerId")] public string? OwnerId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("createdAtUtc")] public required DateTimeOffset CreatedAtUtc { get; init; }
    [JsonPropertyName("invalidatedAtUtc")] public DateTimeOffset? InvalidatedAtUtc { get; init; }
    [JsonPropertyName("validFromUtc")] public DateTimeOffset? ValidFromUtc { get; init; }
    [JsonPropertyName("validUntilUtc")] public DateTimeOffset? ValidUntilUtc { get; init; }
    [JsonPropertyName("supersededByIds")] public required IReadOnlyList<string> SupersededByIds { get; init; }
    [JsonPropertyName("supersedesIds")] public required IReadOnlyList<string> SupersedesIds { get; init; }
    [JsonPropertyName("sourceMessageIds")] public required IReadOnlyList<string> SourceMessageIds { get; init; }

    /// <summary>How often WE surfaced this — the read audit, not a salience score.</summary>
    [JsonPropertyName("readAuditCount")] public required int ReadAuditCount { get; init; }
}

/// <summary>The compiled per-owner working-memory block (Wave C), as the wire carries it.</summary>
internal sealed record WireWorkingMemory
{
    [JsonPropertyName("ownerId")] public required string OwnerId { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("builtAtUtc")] public required DateTimeOffset BuiltAtUtc { get; init; }

    /// <summary>Lets a client tell "unchanged since last session" from "rebuilt identically".</summary>
    [JsonPropertyName("contentHash")] public required string ContentHash { get; init; }
}
