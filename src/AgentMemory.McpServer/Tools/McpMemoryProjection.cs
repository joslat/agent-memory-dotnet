using AgentMemory.Abstractions.Domain;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// Wire shapes for recalled memory, with the stored embedding vectors removed (0.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The tools serialized domain objects directly, so every recall shipped its vectors.</b>
/// <c>Entity</c>, <c>Fact</c>, <c>Preference</c> and <c>ReasoningTrace</c> all carry an embedding —
/// 384 or 1536 floats each — and a recall returning thirty items therefore put tens of thousands of
/// numbers on the wire that no MCP client can use. <c>MemoryOptions.OmitEmbeddingsFromRecall</c>
/// defaults to <c>false</c>, so this was the shipped behaviour on every call.
/// </para>
/// <para>
/// <b>Projected here rather than fixed with <c>[JsonIgnore]</c> on the domain types.</b> Those types
/// are serialized by consumers we do not own, and an attribute would silently change their output
/// too. The cost is that this projection must be kept in step with the domain — which is why it is
/// one shared helper rather than a copy per tool.
/// </para>
/// </remarks>
internal static class McpMemoryProjection
{
    /// <summary>The whole assembled context, embedding-free.</summary>
    internal static object Context(MemoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new
        {
            context.SessionId,
            context.AssembledAtUtc,
            context.Truncated,
            context.LatencyBudgetExceeded,
            context.ResolvedTemporalAsOf,
            recentMessages = context.RecentMessages.Items.Select(Message).ToList(),
            relevantMessages = context.RelevantMessages.Items.Select(Message).ToList(),
            relevantEntities = context.RelevantEntities.Items.Select(Entity).ToList(),
            relevantFacts = context.RelevantFacts.Items.Select(Fact).ToList(),
            relevantPreferences = context.RelevantPreferences.Items.Select(Preference).ToList(),
            similarTraces = context.SimilarTraces.Items.Select(Trace).ToList(),
            // 30.7/30.8. Projected because they are COUNTED: MemoryService includes all three in
            // TotalItemsRetrieved, so omitting them here would report a client N items retrieved and
            // hand back a context missing them -- a count that does not match its own content, which is
            // worse than either the count or the content being wrong alone.
            //
            // Empty on every recall that did not enable firing or forgetting, so an unflagged client
            // sees three empty arrays and nothing else changes.
            dueFacts = context.DueFacts.Items.Select(Fact).ToList(),
            expiringFacts = context.ExpiringFacts.Items.Select(Fact).ToList(),
            // The SUMMARY, never the forgotten facts -- rendering those would undo the forgetting, and
            // an MCP client is exactly the consumer that would treat them as ordinary memory.
            forgottenTopics = context.ForgottenTopics.Select(ForgottenTopic).ToList(),
            context.GraphRagContext,
        };
    }

    internal static object ForgottenTopic(ForgottenTopicSummary summary) => new
    {
        summary.Topic,
        summary.Count,
        summary.OldestUtc,
        summary.AgedOutUtc,
    };

    internal static object Message(Message message) => new
    {
        message.MessageId,
        message.SessionId,
        message.Role,
        message.Content,
        message.TimestampUtc,
        message.Metadata,
    };

    internal static object Entity(Entity entity) => new
    {
        entity.EntityId,
        entity.Name,
        entity.CanonicalName,
        entity.Type,
        entity.Subtype,
        entity.Description,
        entity.Confidence,
        entity.SourceMessageIds,
        entity.Metadata,
    };

    internal static object Fact(Fact fact) => new
    {
        fact.FactId,
        fact.Subject,
        fact.Predicate,
        fact.Object,
        fact.Confidence,
        fact.ValidFrom,
        fact.ValidUntil,
        fact.InvalidatedAtUtc,
        fact.Category,
        fact.SourceMessageIds,
        fact.Metadata,
    };

    internal static object Preference(Preference preference) => new
    {
        preference.PreferenceId,
        preference.Category,
        preference.PreferenceText,
        preference.Confidence,
        preference.SourceMessageIds,
        preference.Metadata,
    };

    /// <summary>
    /// A recalled trace, with its <b>outcome</b> — the half a client actually needs.
    /// </summary>
    /// <remarks>
    /// The resource previously emitted a bare <c>traceCount</c> and no content at all, so procedural
    /// memory was invisible over MCP while still costing a vector search on every recall. Task and
    /// outcome are both carried: a trace rendering its task and dropping its outcome says "you have
    /// done this before" and nothing about how, which is the product gap 7.6 spent five runs finding.
    /// </remarks>
    internal static object Trace(ReasoningTrace trace) => new
    {
        trace.TraceId,
        trace.SessionId,
        trace.Task,
        trace.Outcome,
        trace.Success,
        trace.Kind,
        trace.StartedAtUtc,
        trace.CompletedAtUtc,
    };
}
