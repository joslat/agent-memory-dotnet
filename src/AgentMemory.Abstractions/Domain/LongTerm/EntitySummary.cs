using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// A synthesized description of everything memory holds about one entity (S1).
/// </summary>
/// <remarks>
/// <para>
/// Recall about a well-known entity returns twenty separate facts that each cost context and say one
/// thing. A summary says the same in one item — but a summary is a <i>derived</i> memory, and derived
/// memory is where stores quietly start lying: the sources change, the summary does not, and nothing
/// about it looks any different afterwards.
/// </para>
/// <para>
/// <b>So staleness here is proved, never assumed.</b> <see cref="SourceFingerprint"/> is computed
/// from the exact set of live facts the summary was written from. Before a summary is used, that
/// fingerprint is recomputed from the store; if it does not match — a source superseded, a new fact
/// added, a confidence moved — the summary is <b>not used</b>. It is not repaired on the read path
/// and it is not returned with a caveat, because a caveat is something a caller can ignore.
/// </para>
/// <para>
/// That check is also why this earns its place in a graph rather than a summary column in a
/// relational row: the fingerprint is derived from the very provenance edges the store already keeps,
/// so "is this still true?" is a query rather than a convention someone has to remember to follow.
/// </para>
/// </remarks>
public sealed record EntitySummary
{
    /// <summary>Unique identifier for the summary node.</summary>
    public required string SummaryId { get; init; }

    /// <summary>The entity this summarises.</summary>
    public required string EntityId { get; init; }

    /// <summary>The synthesized text.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// The facts this summary was written from, in the order they were read.
    /// </summary>
    /// <remarks>
    /// Retained rather than merely hashed so a stale summary can say <i>what</i> changed, and so the
    /// <c>EXTRACTED_FROM</c> edges to the underlying sources can be rebuilt without re-synthesising.
    /// </remarks>
    public required IReadOnlyList<string> SourceFactIds { get; init; }

    /// <summary>
    /// Hash of the source facts as they stood when the summary was written.
    /// </summary>
    /// <remarks>
    /// The staleness proof. Recompute it from the store and compare: equal means every source is
    /// still live and unchanged, anything else means this text describes a state of the world that
    /// no longer exists.
    /// </remarks>
    public required string SourceFingerprint { get; init; }

    /// <summary>Owner (R1). Null = shared/global.</summary>
    public string? OwnerId { get; init; }

    /// <summary>When the summary was synthesized.</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>Embedding of <see cref="Content"/>, when one has been generated.</summary>
    public IReadOnlyList<float>? Embedding { get; init; }

    /// <summary>
    /// Computes the fingerprint of a set of source facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers each fact's id, its confidence and whether it has been invalidated — the three things
    /// that change what a truthful summary would say. <b>Confidence is included deliberately:</b>
    /// reinforcement (S2) moves it, and a summary asserting something the store has since grown
    /// doubtful about is exactly the stale shadow this design exists to prevent.
    /// </para>
    /// <para>
    /// Order-independent, because the fingerprint must answer "are these the same facts?" and not
    /// "did they come back in the same order?" — a query plan change would otherwise invalidate every
    /// summary in the store without a single fact having moved.
    /// </para>
    /// </remarks>
    public static string ComputeFingerprint(IEnumerable<EntitySummarySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var canonical = sources
            .Select(s => string.Create(
                CultureInfo.InvariantCulture,
                $"{s.FactId}|{s.Confidence:F6}|{(s.Invalidated ? 1 : 0)}"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var joined = string.Join("\n", canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }
}

/// <summary>
/// One fact's contribution to a summary's fingerprint (S1).
/// </summary>
/// <param name="FactId">The fact.</param>
/// <param name="Confidence">Its confidence at the time of reading.</param>
/// <param name="Invalidated">Whether it has been superseded or otherwise invalidated.</param>
public readonly record struct EntitySummarySource(string FactId, double Confidence, bool Invalidated);
