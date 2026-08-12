using System.Globalization;
using System.Text;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Memory;

/// <summary>
/// Renders a memory block from what the graph currently holds (S4).
/// </summary>
/// <remarks>
/// <para>
/// Deterministic and projection-only. Two renders over the same memory produce the same bytes, so a
/// developer diffing two blocks sees what changed in memory rather than what changed in a sampler.
/// </para>
/// <para>
/// There is no inverse. Nothing here parses a block back into memories, and that absence is the
/// design: a round-trippable block invites an agent to edit it and hand it back, at which point the
/// block becomes the real store and the graph's provenance, trust and supersession records describe
/// something else.
/// </para>
/// </remarks>
internal static class MemoryBlockRenderer
{
    /// <summary>
    /// Builds a block from the supplied memories, newest and most-confident first within each kind.
    /// </summary>
    /// <param name="entities">Entities to include.</param>
    /// <param name="facts">Facts to include. Superseded facts are excluded.</param>
    /// <param name="preferences">Preferences to include.</param>
    /// <param name="renderedAtUtc">Render timestamp.</param>
    /// <param name="ownerId">Owner the block describes.</param>
    /// <param name="maxLines">
    /// Hard cap. Anything beyond it is counted into <see cref="MemoryBlock.OmittedCount"/> rather
    /// than dropped quietly.
    /// </param>
    public static MemoryBlock Render(
        IReadOnlyList<Entity> entities,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<Preference> preferences,
        DateTimeOffset renderedAtUtc,
        string? ownerId = null,
        int maxLines = 50)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

        var all = new List<MemoryBlockLine>();

        all.AddRange(entities
            .OrderByDescending(e => e.Confidence)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new MemoryBlockLine(
                e.EntityId,
                MemoryItemKind.Entity,
                string.IsNullOrWhiteSpace(e.Type) ? e.Name : $"{e.Name} — {e.Type}")));

        all.AddRange(facts
            // A block is what memory believes NOW. Showing a superseded fact beside a live one, with
            // no per-item history in view, presents a retracted claim as current.
            .Where(f => f.InvalidatedAtUtc is null)
            .OrderByDescending(f => f.Confidence)
            .ThenBy(f => f.Subject, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Predicate, StringComparer.OrdinalIgnoreCase)
            .Select(f => new MemoryBlockLine(
                f.FactId,
                MemoryItemKind.Fact,
                $"{f.Subject} {f.Predicate} {f.Object}")));

        all.AddRange(preferences
            .OrderByDescending(p => p.Confidence)
            .ThenBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
            .Select(p => new MemoryBlockLine(
                p.PreferenceId,
                MemoryItemKind.Preference,
                string.IsNullOrWhiteSpace(p.Category) ? p.PreferenceText : $"{p.Category}: {p.PreferenceText}")));

        return new MemoryBlock
        {
            OwnerId = ownerId,
            RenderedAtUtc = renderedAtUtc,
            Lines = all.Take(maxLines).ToList(),
            OmittedCount = Math.Max(0, all.Count - maxLines),
        };
    }

    /// <summary>
    /// Builds a block from memory-history rows (S4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The practical entry point. <c>IMemoryHistoryService</c> already answers "what does memory hold
    /// for this owner" across all three kinds through an audited read path, so the block is a
    /// rendering of an existing query rather than a new way to reach into the store — which is the
    /// point of calling it a projection.
    /// </para>
    /// <para>
    /// Invalidated rows are dropped here as well as being excludable at the query. Belt and braces on
    /// purpose: a caller that forgets <c>IncludeInvalidated = false</c> would otherwise get a block
    /// presenting retracted claims as current, and nothing about it would look wrong.
    /// </para>
    /// </remarks>
    public static MemoryBlock Render(
        IReadOnlyList<MemoryHistoryRecord> records,
        DateTimeOffset renderedAtUtc,
        string? ownerId = null,
        int maxLines = 50)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

        var lines = records
            .Where(r => r.Status != MemoryHistoryStatus.Invalidated && r.InvalidatedAtUtc is null)
            .OrderBy(r => KindOrder(r.Kind))
            .ThenBy(r => r.Summary, StringComparer.OrdinalIgnoreCase)
            .Select(r => new MemoryBlockLine(r.Id, ToItemKind(r.Kind), r.Summary))
            .ToList();

        return new MemoryBlock
        {
            OwnerId = ownerId,
            RenderedAtUtc = renderedAtUtc,
            Lines = lines.Take(maxLines).ToList(),
            OmittedCount = Math.Max(0, lines.Count - maxLines),
        };
    }

    private static int KindOrder(MemoryHistoryKind kind) => kind switch
    {
        MemoryHistoryKind.Entity => 0,
        MemoryHistoryKind.Fact => 1,
        _ => 2,
    };

    private static MemoryItemKind ToItemKind(MemoryHistoryKind kind) => kind switch
    {
        MemoryHistoryKind.Entity => MemoryItemKind.Entity,
        MemoryHistoryKind.Fact => MemoryItemKind.Fact,
        _ => MemoryItemKind.Preference,
    };

    /// <summary>
    /// Renders a block as the text a human reads.
    /// </summary>
    /// <remarks>
    /// Ids are shown, not hidden behind the prose. They are what turns "that fact is wrong" into an
    /// action against a specific memory, which is the whole reason this stayed a read surface: the
    /// correction goes through the audited write path rather than by editing this text.
    /// </remarks>
    public static string ToText(MemoryBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var builder = new StringBuilder();
        builder.Append("# Memory");
        if (!string.IsNullOrWhiteSpace(block.OwnerId))
            builder.Append(" — ").Append(block.OwnerId);
        builder.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"\n_rendered {block.RenderedAtUtc:u} — a snapshot, not a document to edit_\n"));

        foreach (var kind in new[] { MemoryItemKind.Entity, MemoryItemKind.Fact, MemoryItemKind.Preference })
        {
            var lines = block.Lines.Where(l => l.Kind == kind).ToList();
            if (lines.Count == 0) continue;

            builder.Append('\n').Append("## ").Append(kind).Append("\n");
            foreach (var line in lines)
                builder.Append(string.Create(CultureInfo.InvariantCulture, $"- {line.Text}  `{line.MemoryId}`\n"));
        }

        if (block.IsTruncated)
        {
            // Stated in the text itself, not only on the object. A reader who sees a block ending
            // cleanly assumes they have seen everything, and the whole value of this view is that it
            // can be trusted to show what memory holds.
            builder.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"\n_… {block.OmittedCount} more not shown (block limit reached)_\n"));
        }

        if (block.Lines.Count == 0)
            builder.Append("\n_Memory holds nothing for this owner yet._\n");

        return builder.ToString();
    }
}
