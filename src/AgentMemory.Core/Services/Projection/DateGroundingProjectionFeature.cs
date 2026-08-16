using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// Gives date-bearing items their real date, and optionally orders a section by it.
/// </summary>
/// <remarks>
/// <para>
/// The date a memory was stated is in the source message and reaches no product renderer. Where a
/// message carries a <c>sourceTimestamp</c> in its metadata that wins over the storage timestamp,
/// mirroring how the benchmark harness already resolves a display date — because a corpus ingested in
/// one afternoon has storage timestamps that say nothing and source timestamps that say everything.
/// </para>
/// <para>
/// <b>Ordering only fires when it can mean something.</b> A section where fewer than two items carry
/// a date has no chronology to impose, and reordering on a single date would rearrange the retrieval
/// ranking for no gain. Within sections only: no cross-section interleaving and no computed intervals.
/// </para>
/// <para>
/// The repository is optional for the DI reason documented on <see cref="SupersessionProjectionFeature"/>.
/// </para>
/// </remarks>
internal sealed class DateGroundingProjectionFeature(IMessageRepository? messages) : IProjectionFeature
{
    /// <summary>Metadata key carrying the real-world time a message was said.</summary>
    internal const string SourceTimestampKey = "sourceTimestamp";

    public bool IsEnabled(MemoryProjectionOptions options) =>
        (options.GroundDates || options.ChronologicalOrdering) && messages is not null;

    public async Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (messages is null) return;

        var sources = await state.GetSourceMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        if (sources.Count == 0) return;

        var dates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var fact in state.Facts)
        {
            var date = ResolveDate(fact.SourceMessageIds, sources);
            if (date is null) continue;

            dates[fact.FactId] = date.Value;
            if (state.Options.GroundDates)
            {
                var rendered = date.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                state.Annotate(fact.FactId, annotation => annotation with { SourceDate = rendered });
            }
        }

        if (!state.Options.ChronologicalOrdering) return;

        // Fewer than two dated items is no chronology. Reordering anyway would rearrange the retrieval
        // ranking -- which is a real signal -- to express an ordering the section does not have.
        if (dates.Count < 2) return;

        var ordered = state.Facts
            .OrderBy(fact => dates.TryGetValue(fact.FactId, out var date) ? date : DateTimeOffset.MaxValue)
            .ThenBy(fact => fact.FactId, StringComparer.Ordinal)
            .Select(fact => fact.FactId)
            .ToList();

        state.SetSectionOrder(ProjectionSectionKeys.Facts, ordered);
    }

    /// <summary>The earliest real date among an item's sources, preferring metadata over storage time.</summary>
    private static DateTimeOffset? ResolveDate(
        IReadOnlyList<string> sourceMessageIds, IReadOnlyDictionary<string, Message> sources)
    {
        DateTimeOffset? earliest = null;

        foreach (var id in sourceMessageIds)
        {
            if (!sources.TryGetValue(id, out var message)) continue;

            var candidate = ReadSourceTimestamp(message) ?? message.TimestampUtc;
            if (earliest is null || candidate < earliest) earliest = candidate;
        }

        return earliest;
    }

    /// <summary>
    /// The <c>sourceTimestamp</c> metadata value, parsed leniently, or null.
    /// </summary>
    /// <remarks>
    /// Lenient because this metadata is written by adapters and harnesses rather than by a typed
    /// contract, so an unparseable value is a data condition, not a bug — and falling back to the
    /// storage timestamp is strictly better than throwing on a rendering path.
    /// </remarks>
    private static DateTimeOffset? ReadSourceTimestamp(Message message)
    {
        if (!message.Metadata.TryGetValue(SourceTimestampKey, out var raw) || raw is null) return null;

        return raw switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(dateTime.ToUniversalTime(), TimeSpan.Zero),
            string text when DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ => null,
        };
    }
}
