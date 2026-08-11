namespace AgentMemory.Neo4j.Repositories;

/// <summary>
/// How wide an owner-scoped vector search casts its net, and when it casts again.
/// </summary>
/// <remarks>
/// Neo4j's vector index is global, so an owner filter can only be applied <b>after</b> the index has
/// chosen its top-K. The candidates are therefore drawn from every tenant's data and narrowed
/// afterwards, which means a tenant's recall depends on how its neighbours rank.
/// <para>
/// <b>Measured, 2026-08-10</b>, against a sealed 50-question base: 26,236 facts across 50 owners with
/// the historical over-fetch of <c>max(limit*5, limit+50)</c> = 60 at <c>MaxFacts = 10</c>. Probing
/// with each owner's own message, the owner's own facts inside that global top-60 came to a
/// <b>mean of 7, minimum 1</b> — 88% of the budget consumed by other tenants — and one real question
/// retrieved <b>zero</b> from a graph holding 504 of its own facts, all live, all embedded, all
/// scoring above the similarity floor. The old comment claimed an owner "is never starved by
/// higher-scoring foreign rows"; that is not what the data shows.
/// </para>
/// <para>
/// Isolation itself was never in question — no foreign row is ever returned. What degrades silently
/// is <i>recall</i>, and it degrades further with every tenant added, because the over-fetch is a
/// fixed heuristic that does not scale with tenant count.
/// </para>
/// <para>
/// <b>Escalation is restricted to the empty result on purpose.</b> A short-but-non-empty result still
/// answers the question; zero is total failure. Escalating on "short" would tax every small tenant
/// with an extra query on every recall forever, because an owner holding three facts can never fill a
/// ten-row limit. So: one extra query, only when the first pass found nothing, and bounded.
/// </para>
/// </remarks>
internal static class OwnerVectorOverFetch
{
    /// <summary>Historical multiplier, unchanged — this type replaces six hand-copied copies of it.</summary>
    internal const int Factor = 5;

    /// <summary>Historical floor, unchanged.</summary>
    internal const int Floor = 50;

    /// <summary>Widening applied to a scoped search that came back empty.</summary>
    internal const int EscalationFactor = 8;

    /// <summary>
    /// Ceiling on any single vector query, so escalation can never decay into a full scan.
    /// </summary>
    internal const int MaxTopK = 2_000;

    /// <summary>The first, ordinary width. Identical to the expression it replaces.</summary>
    internal static int InitialTopK(int limit, bool hasOwner) =>
        hasOwner ? Math.Max(limit * Factor, limit + Floor) : limit;

    /// <summary>
    /// Whether a second, wider query is worth issuing.
    /// </summary>
    /// <remarks>
    /// Only for a scoped search that returned nothing. Unscoped, an empty result means the corpus
    /// genuinely held nothing above the floor, and a wider query returns the same nothing more slowly.
    /// </remarks>
    internal static bool ShouldEscalate(int returned, bool hasOwner) => hasOwner && returned == 0;

    /// <summary>The widened width, capped.</summary>
    internal static int EscalatedTopK(int currentTopK)
    {
        if (currentTopK >= MaxTopK)
            return MaxTopK;

        // Guard the multiply itself: currentTopK is bounded above by MaxTopK on entry, but the
        // product must not be allowed to overflow into a negative width.
        var widened = (long)currentTopK * EscalationFactor;
        return widened >= MaxTopK ? MaxTopK : (int)widened;
    }
}
