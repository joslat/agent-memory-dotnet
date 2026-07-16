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
/// renders recalled memory as plain text -- previously this formatter had none of them. Matches the Agent
/// Framework adapter's disclosed scope: recalled conversation history (<see cref="RecallResult"/>'s
/// <c>RecentMessages</c>/<c>RelevantMessages</c>) is intentionally NOT delimited or evaluated here either.
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
        AppendMessages(sb, "### Recent Messages", ctx.RecentMessages);
        AppendMessages(sb, "### Relevant Past Messages", ctx.RelevantMessages);
        AppendEntities(sb, ctx.RelevantEntities, opts, logger);
        AppendFacts(sb, ctx.RelevantFacts, opts, logger);
        AppendPreferences(sb, ctx.RelevantPreferences, opts, logger);
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

    private static void AppendMessages(StringBuilder sb, string heading, MemoryContextSection<Message> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine(heading);
        foreach (var msg in section.Items)
            sb.AppendLine($"[{msg.Role}]: {msg.Content}");
        sb.AppendLine();
    }

    private static void AppendEntities(
        StringBuilder sb, MemoryContextSection<Entity> section, MemoryContextFormatterOptions opts, ILogger? logger)
    {
        if (section.Items.Count == 0) return;
        var lines = new List<string>();
        foreach (var entity in section.Items)
        {
            var desc = string.IsNullOrWhiteSpace(entity.Description) ? string.Empty : $" — {entity.Description}";
            var line = $"- {entity.Name} ({entity.Type}){desc}";
            if (Admit("entities", line, entity.Metadata.GetTrustLevel(), opts, logger))
                lines.Add(line);
        }
        if (lines.Count == 0) return;
        sb.AppendLine("### Known Entities");
        sb.AppendLine(RecalledMemoryDelimiter.Wrap("entities", string.Join("\n", lines)));
        sb.AppendLine();
    }

    private static void AppendFacts(
        StringBuilder sb, MemoryContextSection<Fact> section, MemoryContextFormatterOptions opts, ILogger? logger)
    {
        if (section.Items.Count == 0) return;
        var lines = new List<string>();
        foreach (var fact in section.Items)
        {
            var line = $"- {fact.Subject} {fact.Predicate} {fact.Object}";
            if (Admit("facts", line, fact.Metadata.GetTrustLevel(), opts, logger))
                lines.Add(line);
        }
        if (lines.Count == 0) return;
        sb.AppendLine("### Known Facts");
        sb.AppendLine(RecalledMemoryDelimiter.Wrap("facts", string.Join("\n", lines)));
        sb.AppendLine();
    }

    private static void AppendPreferences(
        StringBuilder sb, MemoryContextSection<Preference> section, MemoryContextFormatterOptions opts, ILogger? logger)
    {
        if (section.Items.Count == 0) return;
        var lines = new List<string>();
        foreach (var pref in section.Items)
        {
            var line = $"- [{pref.Category}] {pref.PreferenceText}";
            if (Admit("preferences", line, pref.Metadata.GetTrustLevel(), opts, logger))
                lines.Add(line);
        }
        if (lines.Count == 0) return;
        sb.AppendLine("### User Preferences");
        sb.AppendLine(RecalledMemoryDelimiter.Wrap("preferences", string.Join("\n", lines)));
        sb.AppendLine();
    }
}
