namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Whether ordinary recall honours a fact's valid-time window (<c>valid_from</c>/<c>valid_until</c>).
/// </summary>
/// <remarks>
/// <para>
/// A fact's <c>ValidFrom</c>/<c>ValidUntil</c> persist, are writable through the public
/// API, and the point-in-time recall path already filters on them. <b>Live recall does not</b>: it
/// filters on similarity, <c>invalidated_at</c> and owner, and nothing else. So a fact valid from six
/// months hence is returned by ordinary recall <i>today</i>, and a fact whose <c>valid_until</c> has
/// passed is returned <b>forever</b>.
/// </para>
/// <para>
/// <b>This stopped being harmless in 1.4.0.</b> The gap used to be inert because no extractor wrote
/// validity bounds, so the clauses would have matched nothing. 1.4.0 shipped
/// <c>TemporalValidityMode.Extract</c> — the writer the gap had been waiting for — which makes the
/// defect reachable from configuration.
/// </para>
/// <para>
/// Honouring it also happens to deliver prospective memory's first two mechanisms: <b>expression</b>
/// (a schema that can say "this holds from T") and <b>gating</b> (a read path that respects it).
/// <i>Firing</i> — acting at T with no query — is a scheduler, a different risk class, and explicitly
/// not in scope here.
/// </para>
/// </remarks>
public enum ValidTimeMode
{
    /// <summary>
    /// Ignore valid time on live recall. Today's behaviour, and the default so no existing deployment
    /// silently starts recalling less than it did.
    /// </summary>
    Ignore = 0,

    /// <summary>
    /// Return only facts whose valid-time window contains the present: <c>valid_from</c> is null or in
    /// the past, and <c>valid_until</c> is null or in the future.
    /// </summary>
    Current = 1,
}
