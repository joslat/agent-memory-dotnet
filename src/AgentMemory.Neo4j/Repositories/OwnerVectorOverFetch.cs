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
/// <para>
/// <b>That last argument has a measured counter-example.</b> Question <c>5d3d2817</c> returned
/// <b>2 facts from a 710-fact graph</b> with the gold answer present at coverage 1.00, and both arms
/// answered it wrongly. "Short still answers the question" is true for a small tenant and false for a
/// crowded one, and the returned count alone cannot tell them apart.
/// </para>
/// <para>
/// The rescue is therefore <b>not</b> more widening. Widening is another draw on the same global
/// index, and a tenant losing to 50 neighbours at top-60 usually loses again at top-480. The
/// owner-scoped similarity scan already used as the last resort is the right instrument: its cost
/// scales with <i>one owner's</i> data rather than with the corpus, so the small tenant this argument
/// was protecting pays almost nothing — scanning three facts is cheaper than a second index query —
/// while the crowded tenant gets its true top-K instead of whatever survived the neighbours.
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

    /// <summary>
    /// Whether a scoped search that returned <i>something</i>, but less than asked for, should fall
    /// back to the owner-bounded scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opt-in (<c>MemoryOptions.RescueShortOwnerResults</c>), because it trades latency for recall on
    /// every short result and every recorded measurement was taken without it.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> a fraction of the limit. A ratio invites a threshold nobody can
    /// justify — is 4 of 10 starved? 6 of 10? — and the honest answer is that any shortfall might be
    /// crowding, since the index gave the owner whatever the neighbours left. The scan is bounded by
    /// the owner's own rows either way, so the gate is simply "short", and the cost question is
    /// answered by the scan's shape rather than by guessing a cutoff.
    /// </para>
    /// <para>
    /// Excludes the empty case, which <see cref="ShouldEscalate"/> already owns and which reaches the
    /// same scan through a path that first tries one widened query.
    /// </para>
    /// </remarks>
    internal static bool ShouldRescueShortResult(int returned, int limit, bool hasOwner) =>
        hasOwner && returned > 0 && returned < limit;

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
