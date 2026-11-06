using AgentMemory.Abstractions.Domain;
using AgentMemory.McpServer.Security;

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
/// <para>
/// <b>Every item passes the recall guard (#92, the MCP surface).</b> Each carries <c>trustLevel</c>,
/// <c>instructionLike</c> and <c>withheld</c>; its text is escaped or withheld as the guard says
/// (<see cref="McpRecallGuard"/>). Callers that pass no guard get <see cref="McpRecallGuard.AnnotateOnly"/>:
/// the flags without escaping, for a host with its own content policy.
/// </para>
/// </remarks>
internal static class McpMemoryProjection
{
    /// <summary>The whole assembled context, embedding-free, every item guarded.</summary>
    internal static object Context(MemoryContext context, McpRecallGuard? guard = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        guard ??= McpRecallGuard.AnnotateOnly;
        McpRecallGuard.Verdict? graphRag =
            context.GraphRagContext is null ? null : guard.Check(context.GraphRagContext, MemoryTrustLevel.Untrusted);
        McpRecallGuard.Verdict? workingMemory =
            context.WorkingMemoryBlock is null ? null : guard.Check(context.WorkingMemoryBlock, MemoryTrustLevel.Untrusted);
        return new
        {
            untrustedContent = guard.NoticeText,
            context.SessionId,
            context.AssembledAtUtc,
            context.Truncated,
            context.LatencyBudgetExceeded,
            context.ResolvedTemporalAsOf,
            recentMessages = context.RecentMessages.Items.Select(m => Message(m, guard)).ToList(),
            relevantMessages = context.RelevantMessages.Items.Select(m => Message(m, guard)).ToList(),
            relevantEntities = context.RelevantEntities.Items.Select(e => Entity(e, guard)).ToList(),
            relevantFacts = context.RelevantFacts.Items.Select(f => Fact(f, guard)).ToList(),
            relevantPreferences = context.RelevantPreferences.Items.Select(p => Preference(p, guard)).ToList(),
            similarTraces = context.SimilarTraces.Items.Select(t => Trace(t, guard)).ToList(),
            // 30.7/30.8. Projected because they are COUNTED: MemoryService includes all three in
            // TotalItemsRetrieved, so omitting them here would report a client N items retrieved and
            // hand back a context missing them -- a count that does not match its own content, which is
            // worse than either the count or the content being wrong alone.
            //
            // Empty on every recall that did not enable firing or forgetting, so an unflagged client
            // sees three empty arrays and nothing else changes.
            dueFacts = context.DueFacts.Items.Select(f => Fact(f, guard)).ToList(),
            expiringFacts = context.ExpiringFacts.Items.Select(f => Fact(f, guard)).ToList(),
            // The SUMMARY, never the forgotten facts -- rendering those would undo the forgetting, and
            // an MCP client is exactly the consumer that would treat them as ordinary memory.
            forgottenTopics = context.ForgottenTopics.Select(t => ForgottenTopic(t, guard)).ToList(),
            // A string, as before; null when withheld. Its own flag sits beside it.
            GraphRagContext = graphRag?.Text(context.GraphRagContext),
            graphRagInstructionLike = graphRag?.InstructionLike ?? false,
            // The profile block (on by default): what the host's agent sees as "about this user". Recall
            // already paid to read (and maybe rebuild) it; dropping it here left MCP clients without it.
            // Compiled from extraction output, so it is guarded like every other item and earns no bypass.
            workingMemory = workingMemory?.Text(context.WorkingMemoryBlock),
            workingMemoryInstructionLike = workingMemory?.InstructionLike ?? false,
        };
    }

    internal static object ForgottenTopic(ForgottenTopicSummary summary, McpRecallGuard? guard = null)
    {
        var v = (guard ?? McpRecallGuard.AnnotateOnly).Check(summary.Topic, MemoryTrustLevel.Untrusted);
        return new
        {
            Topic = v.Text(summary.Topic),
            summary.Count,
            summary.OldestUtc,
            summary.AgedOutUtc,
            trustLevel = v.Trust,
            instructionLike = v.InstructionLike,
            withheld = v.Withheld,
        };
    }

    internal static object Message(Message message, McpRecallGuard? guard = null)
    {
        var v = (guard ?? McpRecallGuard.AnnotateOnly).Check(message.Content, message.Metadata);
        return new
        {
            message.MessageId,
            message.SessionId,
            Role = v.Guard.Escape(message.Role),
            Content = v.Text(message.Content),
            message.TimestampUtc,
            Metadata = v.Metadata(message.Metadata),
            trustLevel = v.Trust,
            instructionLike = v.InstructionLike,
            withheld = v.Withheld,
        };
    }

    internal static object Entity(Entity entity, McpRecallGuard? guard = null)
    {
        // The text the in-process adapters judge for an entity: its description, or its name without one.
        var v = (guard ?? McpRecallGuard.AnnotateOnly).Check(McpRecallGuard.TrustOf(entity.Metadata),
            entity.Name, entity.CanonicalName, entity.Type, entity.Subtype, entity.Description);
        return new
        {
            entity.EntityId,
            Name = v.Text(entity.Name),
            CanonicalName = v.Text(entity.CanonicalName),
            Type = v.Text(entity.Type),
            Subtype = v.Text(entity.Subtype),
            Description = v.Text(entity.Description),
            entity.Confidence,
            entity.SourceMessageIds,
            Metadata = v.Metadata(entity.Metadata),
            trustLevel = v.Trust,
            instructionLike = v.InstructionLike,
            withheld = v.Withheld,
        };
    }

    internal static object Fact(Fact fact, McpRecallGuard? guard = null)
    {
        var v = (guard ?? McpRecallGuard.AnnotateOnly).Check(McpRecallGuard.TrustOf(fact.Metadata),
            fact.Subject, fact.Predicate, fact.Object, fact.Category);
        return new
        {
            fact.FactId,
            Subject = v.Text(fact.Subject),
            Predicate = v.Text(fact.Predicate),
            Object = v.Text(fact.Object),
            fact.Confidence,
            fact.ValidFrom,
            fact.ValidUntil,
            fact.OccurredOn,
            fact.InvalidatedAtUtc,
            Category = v.Text(fact.Category),
            fact.SourceMessageIds,
            Metadata = v.Metadata(fact.Metadata),
            trustLevel = v.Trust,
            instructionLike = v.InstructionLike,
            withheld = v.Withheld,
        };
    }

    internal static object Preference(Preference preference, McpRecallGuard? guard = null)
    {
        var v = (guard ?? McpRecallGuard.AnnotateOnly).Check(McpRecallGuard.TrustOf(preference.Metadata),
            preference.Category, preference.PreferenceText);
        return new
        {
            preference.PreferenceId,
            Category = v.Text(preference.Category),
            PreferenceText = v.Text(preference.PreferenceText),
            preference.Confidence,
            preference.SourceMessageIds,
            Metadata = v.Metadata(preference.Metadata),
            trustLevel = v.Trust,
            instructionLike = v.InstructionLike,
            withheld = v.Withheld,
        };
    }

    /// <summary>
    /// A recalled trace, with its <b>outcome</b> — the half a client actually needs.
    /// </summary>
    /// <remarks>
    /// The resource previously emitted a bare <c>traceCount</c> and no content at all, so procedural
    /// memory was invisible over MCP while still costing a vector search on every recall. Task and
    /// outcome are both carried: a trace rendering its task and dropping its outcome says "you have
    /// done this before" and nothing about how, which is the product gap 7.6 spent five runs finding.
    /// </remarks>
    internal static object Trace(ReasoningTrace trace, McpRecallGuard? guard = null)
    {
        var v = (guard ?? McpRecallGuard.AnnotateOnly).Check(McpRecallGuard.TrustOf(trace.Metadata), trace.Task, trace.Outcome);
        return new
        {
            trace.TraceId,
            trace.SessionId,
            Task = v.Text(trace.Task),
            Outcome = v.Text(trace.Outcome),
            trace.Success,
            trace.Kind,
            trace.StartedAtUtc,
            trace.CompletedAtUtc,
            trustLevel = v.Trust,
            instructionLike = v.InstructionLike,
            withheld = v.Withheld,
        };
    }
}
