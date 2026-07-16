using System.Text;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Security;

namespace AgentMemory.Core.Services;

/// <summary>
/// Renders a <see cref="RecallResult"/> into a Markdown "Memory Context" block. This presentation
/// logic lives in Core so framework adapters (e.g. the Semantic Kernel plugin) remain thin and do
/// not carry formatting code of their own.
/// </summary>
/// <remarks>
/// #92 Phase 6: entities/facts/preferences/GraphRAG are now delimited (<see cref="RecalledMemoryDelimiter"/>,
/// #92 Phase 1) and individually evaluated by an instruction-like-content admission check (#92 Phase 2/3),
/// bringing the same protections the Agent Framework adapter has had since those phases to any adapter that
/// renders recalled memory as plain text -- previously this formatter had none of them. Recalled
/// conversation history (<see cref="RecallResult"/>'s <c>RecentMessages</c>/<c>RelevantMessages</c>)
/// remains intentionally NOT delimited or admission-checked (a disclosed, narrower gap -- this is genuinely
/// recalled transcript, not a "memory object" being injected as if authoritative), but its ROLE label is
/// gated (#92 Phase 7): see <see cref="AppendMessages"/>.
/// </remarks>
internal static class MemoryContextFormatter
{
    /// <summary>
    /// Formats the recalled context as Markdown, or returns an empty string when nothing was retrieved.
    /// </summary>
    public static string FormatRecallResult(
        RecallResult result, MemoryContextFormatterOptions? options = null, ILogger? logger = null)
    {
        if (result.TotalItemsRetrieved == 0)
            return string.Empty;

        var opts = options ?? new MemoryContextFormatterOptions();
        var ctx = result.Context;
        var sb = new StringBuilder();
        sb.AppendLine("## Memory Context");

        // Blend policy (plan §12.5): GraphRagOnly / GraphRagThenMemory render the graph block first;
        // all other modes keep it after the memory-derived sections.
        bool graphFirst = ctx.BlendMode is RetrievalBlendMode.GraphRagOnly or RetrievalBlendMode.GraphRagThenMemory;

        if (graphFirst) AppendGraphRag(sb, ctx.GraphRagContext, opts, logger);
        AppendMessages(sb, "### Recent Messages", ctx.RecentMessages, opts);
        AppendMessages(sb, "### Relevant Past Messages", ctx.RelevantMessages, opts);
        AppendCategory(sb, "entities", "### Known Entities", ctx.RelevantEntities.Items,
            e => string.IsNullOrWhiteSpace(e.Description) ? $"- {e.Name} ({e.Type})" : $"- {e.Name} ({e.Type}) — {e.Description}",
            e => e.Metadata.GetTrustLevel(), opts, logger);
        AppendCategory(sb, "facts", "### Known Facts", ctx.RelevantFacts.Items,
            f => $"- {f.Subject} {f.Predicate} {f.Object}",
            f => f.Metadata.GetTrustLevel(), opts, logger);
        AppendCategory(sb, "preferences", "### User Preferences", ctx.RelevantPreferences.Items,
            p => $"- [{p.Category}] {p.PreferenceText}",
            p => p.Metadata.GetTrustLevel(), opts, logger);
        if (!graphFirst) AppendGraphRag(sb, ctx.GraphRagContext, opts, logger);
        return sb.ToString().TrimEnd();
    }

    // Evaluates one candidate block's content against instruction-like-content admission (#92 Phase 2),
    // with a trust-level bypass (#92 Phase 3) -- the same per-item granularity rationale as the Agent
    // Framework adapter's Admit: one flagged item must not silently drop other, unrelated items rendered
    // alongside it (callers check per item, not per joined block).
    private static bool Admit(string category, string content, MemoryTrustLevel trustLevel,
        MemoryContextFormatterOptions opts, ILogger? logger)
    {
        // Not delegated to the shared RecalledMemoryAdmission.ShouldAdmit (used by Neo4jTextSearch, which
        // has no logger to plumb through): this call site additionally distinguishes "admitted outright"
        // from "flagged but included under Permissive" for observability, which the shared boolean-only
        // decision doesn't expose -- re-deriving that distinction here would mean evaluating the same
        // checks twice, so this keeps its own self-contained sequence instead.
        if (trustLevel >= opts.MinimumTrustForAdmissionBypass) return true;

        if (!InstructionLikeContentDetector.IsMatch(content)) return true;

        if (!opts.Strict)
        {
            logger?.LogDebug(
                "Recalled memory item in category '{Category}' flagged as instruction-like content " +
                "but included (Strict=false).", category);
            return true;
        }

        logger?.LogWarning(
            "Excluded a recalled memory item in category '{Category}' from context: instruction-like content.",
            category);
        return false;
    }

    private static void AppendGraphRag(
        StringBuilder sb, string? graphRagContext, MemoryContextFormatterOptions opts, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(graphRagContext)) return;
        if (!Admit("graphrag", graphRagContext, MemoryTrustLevel.Untrusted, opts, logger)) return;
        sb.AppendLine("### Graph Context");
        sb.AppendLine(RecalledMemoryDelimiter.Wrap("graphrag", graphRagContext));
    }

    // #92 Phase 7: message CONTENT is deliberately not delimited/admission-checked here (a disclosed,
    // narrower gap -- this is genuinely recalled conversation transcript, not a "memory object" being
    // injected as if authoritative), but a message's ROLE label still is: a privileged role ("system"/
    // "tool") persisted via a caller-facing tool (memory_store_message, Neo4jMemoryPlugin.AddMessageAsync)
    // must not resurface with its label unchanged unless sufficiently trusted.
    private static void AppendMessages(
        StringBuilder sb, string heading, MemoryContextSection<Message> section, MemoryContextFormatterOptions opts)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine(heading);
        foreach (var msg in section.Items)
        {
            var effectiveRole = RecalledMessageRoleGate.EffectiveRole(
                msg.Role, msg.Metadata.GetTrustLevel(), opts.MinimumTrustForSystemRole);
            sb.AppendLine($"[{effectiveRole}]: {msg.Content}");
        }
        sb.AppendLine();
    }

    // Renders a list-shaped category's (entities/facts/preferences) admitted items into one delimited,
    // heading-prefixed block -- mirroring AgentMemory.AgentFramework.Mapping.MafTypeMapper's generic
    // CategoryMessages<T> helper for the same three categories, instead of three near-identical
    // hand-written loops that could silently drift apart.
    private static void AppendCategory<T>(
        StringBuilder sb, string category, string heading, IReadOnlyList<T> items,
        Func<T, string> describe, Func<T, MemoryTrustLevel> getTrustLevel,
        MemoryContextFormatterOptions opts, ILogger? logger)
    {
        if (items.Count == 0) return;
        var lines = new List<string>();
        foreach (var item in items)
        {
            var line = describe(item);
            if (Admit(category, line, getTrustLevel(item), opts, logger))
                lines.Add(line);
        }
        if (lines.Count == 0) return;
        sb.AppendLine(heading);
        sb.AppendLine(RecalledMemoryDelimiter.Wrap(category, string.Join("\n", lines)));
        sb.AppendLine();
    }
}
