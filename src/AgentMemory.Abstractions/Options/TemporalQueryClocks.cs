namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Which clocks a query-time temporal resolution should bind, when
/// <c>MemoryOptions.ResolveTemporalQueries</c> routes a turn to bitemporal recall.
/// </summary>
/// <remarks>
/// <para>
/// Two different questions wear the same grammar, and the parser cannot separate them:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <i>"What did I buy ten days ago?"</i> asks about the <b>world</b> at a past instant, answered with
/// everything known now. Valid time.
/// </description></item>
/// <item><description>
/// <i>"What did I think back in March?"</i> asks about <b>belief</b> at a past instant — what was true
/// then, as known then. Both clocks.
/// </description></item>
/// </list>
/// <para>
/// <b>The defaults are chosen on which mistake is survivable, because the failure modes are not
/// symmetric.</b> Answering a past-world question with a later correction applied is usually what the
/// user wanted anyway. Binding the transaction clock when it was not wanted is total and silent: it
/// excludes every row created after the resolved instant, and <c>created_at</c> is <i>ingestion</i>
/// time on any host that imported, migrated or backfilled its history. Such a host asks "what happened
/// last month", gets an empty context with no error, and reads it as the memory having nothing.
/// </para>
/// <para>
/// So the default is <see cref="ValidTimeOnly"/> and belief reconstruction is asked for explicitly.
/// This changes no shipped behaviour: query-time resolution is itself opt-in, so a host that never
/// enabled it takes the path it always did.
/// </para>
/// </remarks>
public enum TemporalQueryClocks
{
    /// <summary>
    /// Bind only valid time: what was true at the resolved instant, according to everything known now.
    /// <b>The default.</b>
    /// </summary>
    ValidTimeOnly = 0,

    /// <summary>
    /// Bind both clocks: what was true at the resolved instant <i>as it was known then</i>. Correct for
    /// belief reconstruction and audit questions.
    /// </summary>
    /// <remarks>
    /// Only meaningful where <c>created_at</c> records when the system genuinely learned a fact. Where
    /// it records when a corpus was imported, this excludes the whole store for any past instant.
    /// </remarks>
    ValidAndTransactionTime = 1,
}
