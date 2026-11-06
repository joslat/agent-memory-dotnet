using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer.Tools;
using AgentMemory.McpServer.Security;

namespace AgentMemory.McpServer.Resources;

/// <summary>
/// MCP resource that returns assembled memory context for a session.
/// </summary>
[McpServerResourceType]
internal sealed class ContextResource
{
    [McpServerResource(UriTemplate = "memory://context/{session_id}", Name = "memory_context", MimeType = "application/json"),
     Description("Returns assembled memory context for a given session, including recent messages, relevant entities, facts, and preferences.")]
    public static async Task<string> GetContext(
        IMemoryContextAssembler contextAssembler,
        IOptions<AgentMemoryMcpOptions> options,
        [Description("Session identifier")] string session_id,
        [Description("Query text to match relevant context")] string? query = null,
        [Description("Maximum number of recent messages to include")] int maxRecentMessages = 20,
        [Description("Owner/user identifier (optional). When set, the recalled entities/facts/preferences are confined to that owner's plus shared (un-owned) memory; null = all owners (unscoped/admin). Set it in multi-tenant deployments to prevent cross-owner reads (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        userId = McpOwnerPin.ForResource(options.Value, userId);
        // R1: scope the recall to the owner so a multi-tenant client can't read other owners' memory
        // content via the context resource. null userId ⇒ unscoped (admin/single-tenant).
        var request = new RecallRequest
        {
            SessionId = session_id,
            UserId = userId,
            Query = query ?? "",
            Options = new RecallOptions { MaxRecentMessages = maxRecentMessages }
        };

        var context = await contextAssembler.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false);

        // Recalled text, guarded as the recall tools guard it (#92, the MCP surface).
        var guard = McpRecallGuard.From(options.Value);
        McpRecallGuard.Verdict? graphRag = context.GraphRagContext is null ? null : guard.Check(context.GraphRagContext, MemoryTrustLevel.Untrusted);
        return ToolJsonContext.Serialize(new
        {
            untrustedContent = McpRecallGuard.Notice,
            sessionId = session_id,
            recentMessageCount = context.RecentMessages.Items.Count,
            relevantMessageCount = context.RelevantMessages.Items.Count,
            entityCount = context.RelevantEntities.Items.Count,
            factCount = context.RelevantFacts.Items.Count,
            preferenceCount = context.RelevantPreferences.Items.Count,
            traceCount = context.SimilarTraces.Items.Count,
            recentMessages = context.RecentMessages.Items.Select(m => (m, v: guard.Check(m.Content, m.Metadata))).Select(x => new
            {
                id = x.m.MessageId,
                role = x.v.Guard.Escape(x.m.Role),
                content = x.v.Text(x.m.Content?.Length > 500 ? x.m.Content[..500] + "..." : x.m.Content),
                timestamp = x.m.TimestampUtc,
                trustLevel = x.v.Trust, instructionLike = x.v.InstructionLike, withheld = x.v.Withheld,
            }),
            entities = context.RelevantEntities.Items.Select(e => (e, v: guard.Check(McpRecallGuard.TrustOf(e.Metadata), e.Name, e.Type, e.Description))).Select(x => new
            {
                id = x.e.EntityId,
                name = x.v.Text(x.e.Name),
                type = x.v.Text(x.e.Type),
                trustLevel = x.v.Trust, instructionLike = x.v.InstructionLike, withheld = x.v.Withheld,
            }),
            facts = context.RelevantFacts.Items.Select(f => (f, v: guard.Check(McpRecallGuard.TrustOf(f.Metadata), f.Subject, f.Predicate, f.Object))).Select(x => new
            {
                id = x.f.FactId,
                subject = x.v.Text(x.f.Subject),
                predicate = x.v.Text(x.f.Predicate),
                @object = x.v.Text(x.f.Object),
                trustLevel = x.v.Trust, instructionLike = x.v.InstructionLike, withheld = x.v.Withheld,
            }),
            preferences = context.RelevantPreferences.Items.Select(p => (p, v: guard.Check(McpRecallGuard.TrustOf(p.Metadata), p.PreferenceText, p.Category))).Select(x => new
            {
                id = x.p.PreferenceId,
                preference = x.v.Text(x.p.PreferenceText),
                category = x.v.Text(x.p.Category),
                trustLevel = x.v.Trust, instructionLike = x.v.InstructionLike, withheld = x.v.Withheld,
            }),
            // 0.7. Every sibling category projected its content and this one projected only a count,
            // so procedural memory was invisible through the resource while still costing a vector
            // search on every recall. Outcome travels with task deliberately: a trace that renders
            // what was attempted and drops how it went says "you have done this before" and nothing
            // useful -- the product gap 7.6 spent five runs finding.
            traces = context.SimilarTraces.Items.Select(t => (t, v: guard.Check($"{t.Task} {t.Outcome}", t.Metadata))).Select(x => new
            {
                id = x.t.TraceId,
                task = x.v.Text(x.t.Task),
                outcome = x.v.Text(x.t.Outcome),
                success = x.t.Success,
                kind = x.t.Kind,
                trustLevel = x.v.Trust, instructionLike = x.v.InstructionLike, withheld = x.v.Withheld,
            }),
            graphRagContext = graphRag?.Text(context.GraphRagContext),
            graphRagInstructionLike = graphRag?.InstructionLike ?? false,
            assembledAtUtc = context.AssembledAtUtc
        });
    }
}
