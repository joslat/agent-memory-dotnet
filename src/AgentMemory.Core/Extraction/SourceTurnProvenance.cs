namespace AgentMemory.Core.Extraction;

/// <summary>
/// Resolves a reported 1-based turn number to the single source message it names.
/// </summary>
/// <remarks>
/// <para>
/// <c>EXTRACTED_FROM</c> is written per ingestion batch: every item is linked to every message the
/// extraction call saw. On the evaluation corpus a fact links to a mean of <b>12</b> source messages
/// and as many as 30, which is broad enough that <b>any attribution metric derived from that edge is
/// satisfied by construction</b> — "is the true source among the linked messages?" is trivially yes
/// across thirty of them, so the metric can never fail and therefore measures nothing.
/// </para>
/// <para>
/// The turn number is positional by design. The transcript is rendered from the same ordered message
/// list the source-message ids are derived from, so turn <c>N</c> is index <c>N-1</c> — a direct index
/// rather than a lookup that could silently mismatch.
/// </para>
/// <para>
/// <b>Falls back rather than failing.</b> An absent, zero, negative or out-of-range turn keeps the
/// batch links. A model that reports a turn number for a conversation of five as <c>12</c> has told us
/// nothing, and replacing real-if-coarse provenance with a fabricated precise one is strictly worse:
/// coarse provenance is recoverable, wrong provenance is indistinguishable from right.
/// </para>
/// </remarks>
internal static class SourceTurnProvenance
{
    /// <summary>
    /// The message ids to attribute an item to: the single named turn, or
    /// <paramref name="batchMessageIds"/> unchanged when no usable turn was reported.
    /// </summary>
    internal static IReadOnlyList<string> Resolve(
        int? sourceTurn, IReadOnlyList<string> batchMessageIds)
    {
        if (sourceTurn is not { } turn) return batchMessageIds;
        if (turn < 1 || turn > batchMessageIds.Count) return batchMessageIds;
        return [batchMessageIds[turn - 1]];
    }

    /// <summary>
    /// Whether <paramref name="sourceTurn"/> actually narrowed the attribution, for telemetry and for
    /// the falsifier — a resolver that silently never fires looks identical to one that always does.
    /// </summary>
    internal static bool Narrowed(int? sourceTurn, IReadOnlyList<string> batchMessageIds) =>
        sourceTurn is { } turn && turn >= 1 && turn <= batchMessageIds.Count
        && batchMessageIds.Count > 1;
}
