namespace AgentMemory.AgentFramework.Recall;

/// <summary>
/// The default <see cref="IAutomaticRecallPolicy"/>: full configured recall on every real turn, and
/// recent messages only on a turn that is nothing but a greeting or acknowledgement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the default.</b> Measured on the hermetic <c>PERF-R-01</c> scenario — a greeting-only
/// turn at shipped defaults — recall issued <b>13 Cypher queries and 12 read transactions, plus an
/// embedding round trip, to retrieve 11 items</b>. Ten of those items were recent messages, which need
/// no vector at all; the eleventh was a single preference. The owner-starvation rescue added in 1.4.0
/// and 1.4.1 makes it worse, not better: with no entities, facts or traces to find, each of those three
/// searches returns empty, escalates to a widened index query, and then falls back to an owner-bounded
/// scan — three queries each, all guaranteed to find nothing, because there is nothing to find.
/// </para>
/// <para>
/// <b>Why it narrows rather than skips.</b> The obvious design — skip recall entirely on a trivial turn
/// — also drops <c>RecentMessages</c>, and "ok, go ahead" is exactly the turn where the agent most needs
/// the conversation so far to know what it is agreeing to. Narrowing keeps that context and still elides
/// the embedding and every vector search, because <c>MemoryContextAssembler</c> and
/// <c>Neo4jMemoryContextProvider</c> both gate the embedding on whether a category consuming it survived.
/// <see cref="HeuristicAutomaticRecallPolicy"/> remains available for hosts that do want the full skip.
/// </para>
/// <para>
/// <b>Behaviour change, stated plainly.</b> On a greeting-only turn a host previously received whatever
/// entities/facts/preferences/traces matched; it now receives recent messages only. That is a deliberate
/// trade — those items were retrieved for a turn carrying no question — and it is the only difference.
/// Every other turn is byte-identical to <see cref="ConfiguredAutomaticRecallPolicy"/>. A host that wants
/// the previous behaviour registers it explicitly after <c>AddAgentMemoryFramework</c>, which is the
/// documented replacement seam:
/// <c>services.AddScoped&lt;IAutomaticRecallPolicy, ConfiguredAutomaticRecallPolicy&gt;()</c>.
/// </para>
/// <para>
/// Deterministic and free: no model call, no regex backtracking, one linear pass over the turn's words.
/// </para>
/// </remarks>
public sealed class TrivialTurnRecallPolicy : IAutomaticRecallPolicy
{
    /// <inheritdoc/>
    public ValueTask<AutomaticRecallDecision> DecideAsync(
        AutomaticRecallContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = string.Join(" ", context.Messages.Select(m => m.Text ?? string.Empty)).Trim();

        if (!TrivialTurnDetector.IsGreetingOrAcknowledgementOnly(query))
            return ValueTask.FromResult(AutomaticRecallDecision.Recall);

        return ValueTask.FromResult(new AutomaticRecallDecision
        {
            // Recall still runs -- this is a narrowing, not a skip. RecentMessages is session-scoped and
            // time-ordered, so it needs no query vector, and the embedding gate downstream elides the
            // provider round trip precisely because no vector category survives here.
            ShouldRecall = true,
            Categories = AutomaticRecallCategories.RecentMessages,
        });
    }
}
