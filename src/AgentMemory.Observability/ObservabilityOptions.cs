namespace AgentMemory.Observability;

/// <summary>
/// Options for the observability decorators.
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>
    /// Whether to emit the raw owner/user identifier as a span tag. <see langword="false"/> by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tenant identifier in a trace is tenant data in a system that is usually less
    /// access-controlled than the database it came from, retained on a different schedule, and often
    /// exported to a third party. The rest of this codebase already takes that position: every
    /// owner-scoped vector search tags <c>memory.vector.owner_scoped</c> as a <b>boolean</b> rather
    /// than the identifier, which answers the operational question ("was this scoped?") without
    /// carrying the value.
    /// </para>
    /// <para>
    /// When <see langword="false"/>, a <c>memory.owner_scoped</c> boolean is emitted instead, so a
    /// reader can still tell a scoped operation from an unscoped one.
    /// </para>
    /// <para>
    /// <b>Off is a change from previous behaviour</b>, which always emitted the identifier. Hosts that
    /// correlate traces by user — and have decided their identifier is safe to export — turn it back on
    /// deliberately here, which is the point: it should be a decision rather than a default.
    /// </para>
    /// </remarks>
    public bool IncludeOwnerIdInTelemetry { get; set; }
}
