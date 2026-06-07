using System.Text;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services;

/// <summary>
/// Renders a <see cref="RecallResult"/> into a Markdown "Memory Context" block. This presentation
/// logic lives in Core so framework adapters (e.g. the Semantic Kernel plugin) remain thin and do
/// not carry formatting code of their own.
/// </summary>
public static class MemoryContextFormatter
{
    /// <summary>
    /// Formats the recalled context as Markdown, or returns an empty string when nothing was retrieved.
    /// </summary>
    public static string FormatRecallResult(RecallResult result)
    {
        if (result.TotalItemsRetrieved == 0)
            return string.Empty;

        var ctx = result.Context;
        var sb = new StringBuilder();
        sb.AppendLine("## Memory Context");

        // Blend policy (plan §12.5): GraphRagOnly / GraphRagThenMemory render the graph block first;
        // all other modes keep it after the memory-derived sections.
        bool graphFirst = ctx.BlendMode is RetrievalBlendMode.GraphRagOnly or RetrievalBlendMode.GraphRagThenMemory;

        if (graphFirst) AppendGraphRag(sb, ctx.GraphRagContext);
        AppendMessages(sb, "### Recent Messages", ctx.RecentMessages);
        AppendMessages(sb, "### Relevant Past Messages", ctx.RelevantMessages);
        AppendEntities(sb, ctx.RelevantEntities);
        AppendFacts(sb, ctx.RelevantFacts);
        AppendPreferences(sb, ctx.RelevantPreferences);
        if (!graphFirst) AppendGraphRag(sb, ctx.GraphRagContext);
        return sb.ToString().TrimEnd();
    }

    private static void AppendGraphRag(StringBuilder sb, string? graphRagContext)
    {
        if (string.IsNullOrWhiteSpace(graphRagContext)) return;
        sb.AppendLine("### Graph Context");
        sb.AppendLine(graphRagContext);
    }

    private static void AppendMessages(StringBuilder sb, string heading, MemoryContextSection<Message> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine(heading);
        foreach (var msg in section.Items)
            sb.AppendLine($"[{msg.Role}]: {msg.Content}");
        sb.AppendLine();
    }

    private static void AppendEntities(StringBuilder sb, MemoryContextSection<Entity> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine("### Known Entities");
        foreach (var entity in section.Items)
        {
            var desc = string.IsNullOrWhiteSpace(entity.Description) ? string.Empty : $" — {entity.Description}";
            sb.AppendLine($"- {entity.Name} ({entity.Type}){desc}");
        }
        sb.AppendLine();
    }

    private static void AppendFacts(StringBuilder sb, MemoryContextSection<Fact> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine("### Known Facts");
        foreach (var fact in section.Items)
            sb.AppendLine($"- {fact.Subject} {fact.Predicate} {fact.Object}");
        sb.AppendLine();
    }

    private static void AppendPreferences(StringBuilder sb, MemoryContextSection<Preference> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine("### User Preferences");
        foreach (var pref in section.Items)
            sb.AppendLine($"- [{pref.Category}] {pref.PreferenceText}");
        sb.AppendLine();
    }
}
