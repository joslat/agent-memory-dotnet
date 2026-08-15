using System.Text;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// The one place a projection decision becomes text. Three surfaces call it; none of them re-decides.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is the point of the whole layer.</b> Three surfaces render recalled memory — the Core
/// Markdown formatter, the Agent Framework <c>ChatMessage</c> mapper, and the benchmark prompt builder
/// — and every rendering fix used to land three times or rot in two. The recorded case: a
/// procedure-trust clause was fixed in the benchmark harness while the product kept shipping the
/// contradiction, and the trace section itself was invisible to two of four surfaces for a whole phase.
/// Annotations are computed once by the pipeline and turned into strings once, here.
/// </para>
/// <para>
/// <b>Every method is an identity when there is no projection.</b> Null <see cref="ProjectedContext"/>
/// or an unannotated id returns the input unchanged, so a call site can be unconditional and the
/// off-state still produces byte-identical output — which the sealed fingerprints assert.
/// </para>
/// <para>
/// Quotes and supersession notes are <b>recalled content</b>. They are rendered into the line, which
/// each surface then admits and delimits exactly as it already did — never emitted as new
/// system-authority text.
/// </para>
/// </remarks>
internal static class ProjectionRenderer
{
    /// <summary>
    /// Decorates one rendered line with whatever projection knows about that item.
    /// </summary>
    /// <remarks>
    /// Order is fixed and meaningful: the match-quality marker leads (it qualifies everything that
    /// follows), then the item's own text, then the date it was said, then what it used to say, then
    /// the sentence it came from. A reader scanning left to right meets the caveat before the claim.
    /// </remarks>
    public static string AnnotateLine(string line, string itemId, ProjectedContext? projection)
    {
        if (projection is null) return line;
        if (!projection.Annotations.TryGetValue(itemId, out var annotation)) return line;

        var builder = new StringBuilder(line.Length + 96);

        // The marker goes INSIDE the leading "- " so the list stays a list.
        var bulletPrefix = line.StartsWith("- ", StringComparison.Ordinal) ? "- " : string.Empty;
        var body = bulletPrefix.Length > 0 ? line[bulletPrefix.Length..] : line;

        builder.Append(bulletPrefix);
        if (annotation.IsNearMiss)
        {
            builder.Append(annotation.Score is { } score
                ? $"[closest match, {score:F2}] "
                : "[closest match] ");
        }

        builder.Append(body);

        if (!string.IsNullOrWhiteSpace(annotation.SourceDate))
            builder.Append(" (").Append(annotation.SourceDate).Append(')');

        if (!string.IsNullOrWhiteSpace(annotation.SupersessionNote))
            builder.Append(' ').Append(annotation.SupersessionNote);

        if (!string.IsNullOrWhiteSpace(annotation.ProcedureShape))
            builder.Append(' ').Append(annotation.ProcedureShape);

        if (!string.IsNullOrWhiteSpace(annotation.SourceQuote))
            builder.Append(" — said: \"").Append(annotation.SourceQuote).Append('"');

        return builder.ToString();
    }

    /// <summary>
    /// The block text that belongs above a section, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Blocks are joined with newlines rather than returned separately because a section can carry
    /// both a no-direct-match line and one or more conflict blocks, and every surface would otherwise
    /// need its own loop to place them.
    /// </remarks>
    public static string? SectionPreamble(string sectionKey, ProjectedContext? projection)
    {
        if (projection is null || projection.Blocks.Count == 0) return null;

        var texts = projection.Blocks
            .Where(block => string.Equals(block.SectionKey, sectionKey, StringComparison.Ordinal))
            .Select(block => block.Text)
            .ToList();

        return texts.Count == 0 ? null : string.Join("\n", texts);
    }

    /// <summary>
    /// Reorders a section's items when projection computed an order for it; identity otherwise.
    /// </summary>
    /// <remarks>
    /// Items the order does not mention keep their retrieval position at the end rather than being
    /// dropped — an ordering feature that could lose an item would be a retrieval bug wearing a
    /// rendering costume.
    /// </remarks>
    public static IReadOnlyList<T> Reorder<T>(
        string sectionKey,
        IReadOnlyList<T> items,
        Func<T, string> idOf,
        ProjectedContext? projection)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(idOf);

        if (projection is null) return items;
        if (!projection.SectionOrder.TryGetValue(sectionKey, out var order) || order.Count == 0) return items;

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < order.Count; index++) position[order[index]] = index;

        return [.. items.OrderBy(item => position.TryGetValue(idOf(item), out var at) ? at : int.MaxValue)];
    }
}
