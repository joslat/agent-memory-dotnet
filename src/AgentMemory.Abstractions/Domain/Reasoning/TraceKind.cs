namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// What a stored <see cref="ReasoningTrace"/> is <b>for</b>.
/// </summary>
/// <remarks>
/// <para>
/// A trace and a procedure are the same underlying record read two ways. An <b>episode</b> says what
/// happened once — retrieved by when it happened, read as a claim. A <b>procedure</b> says what to do
/// next time — retrieved by similarity of the <i>task</i>, and replayed as steps. Same incident,
/// different retrieval key, which is what makes them different kinds rather than one kind named twice.
/// </para>
/// <para>
/// <b>Named <c>trace_kind</c> on the node, never <c>kind</c>.</b> <c>kind</c> already means
/// "audit-node discriminator" both here and upstream, and overloading a property whose meaning is
/// shared with another implementation is the changed-semantics hazard that a schema-parity check
/// exists to catch. A new property is ungated by the parity verifier; a new label or edge is not.
/// </para>
/// </remarks>
public enum TraceKind
{
    /// <summary>
    /// An ordinary recorded episode. The default, and what every existing trace is.
    /// </summary>
    Episode = 0,

    /// <summary>
    /// A trace promoted to a reusable procedure: retrievable by task similarity and exempt from
    /// recency-based retention pruning.
    /// </summary>
    /// <remarks>
    /// The exemption is not a nicety. <c>PruneSessionTraces</c> orders by <c>started_at</c> with age as
    /// its <b>only</b> criterion and fires on every trace creation once a per-session cap is set — so
    /// without it, promotion is silently undone by recency and the capability does not exist.
    /// </remarks>
    Procedure = 1,
}
