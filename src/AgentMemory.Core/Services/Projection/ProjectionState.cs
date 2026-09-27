using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// The working surface one projection pass shares: what was recalled, what it scored, and the
/// annotations/blocks the features are building up.
/// </summary>
/// <remarks>
/// <para>
/// Mutable and single-pass by design. Features run in a fixed order against one instance and each
/// contributes part of the same annotation — a fact can carry a score, a supersession note, a quote
/// and a date, written by four different features — so the alternative (each feature returning its
/// own immutable slice, merged afterwards) would mean writing a merge function whose only job is to
/// re-assemble what a shared builder gives for free.
/// </para>
/// <para>
/// Holds the <b>post-budget</b> section lists deliberately: projection runs after truncation, so the
/// reads two of the features perform are paid only for items that actually reached the prompt.
/// </para>
/// </remarks>
internal sealed class ProjectionState
{
    private readonly Dictionary<string, ProjectedItemAnnotation> _annotations =
        new(StringComparer.Ordinal);
    private readonly List<ProjectedBlock> _blocks = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _sectionOrder =
        new(StringComparer.Ordinal);

    public required MemoryProjectionOptions Options { get; init; }

    /// <summary>The resolved owner scope, or null for an unscoped recall.</summary>
    public required MemoryScope? Scope { get; init; }

    public required IReadOnlyList<Entity> Entities { get; init; }
    public required IReadOnlyList<Fact> Facts { get; init; }
    public required IReadOnlyList<Preference> Preferences { get; init; }
    public required IReadOnlyList<ReasoningTrace> Traces { get; init; }
    public required IReadOnlyList<Message> RecentMessages { get; init; }
    public required IReadOnlyList<Message> RelevantMessages { get; init; }

    /// <summary>
    /// Retrieval scores per section. <b>Empty means unscoreable</b>, never "scored zero" — a section
    /// whose provider does not implement the scored contract must produce no near-miss marks at all
    /// rather than marks derived from a placeholder.
    /// </summary>
    public required IReadOnlyList<(Entity Entity, double Score)> EntityScores { get; init; }

    public required IReadOnlyList<(Fact Fact, double Score)> FactScores { get; init; }

    public required IReadOnlyList<(Preference Preference, double Score)> PreferenceScores { get; init; }

    public required IReadOnlyList<(ReasoningTrace Trace, double Score)> TraceScores { get; init; }

    private Task<IReadOnlyDictionary<string, Message>>? _sourceMessages;

    /// <summary>
    /// The source messages behind the recalled items, fetched <b>once</b> however many features ask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Memoises the <see cref="Task"/> rather than the result, so two features that both need source
    /// messages — quotes and date grounding — share a single round trip even when they run
    /// concurrently. Awaiting a completed task twice is free; issuing the query twice is not, and
    /// "one extra read per recall per read-feature" is a budget this design states and tests.
    /// </para>
    /// <para>
    /// Ids come from every long-term item's <c>SourceMessageIds</c>, deduplicated: one utterance
    /// commonly produced several facts, and fetching it once per fact would multiply the read by the
    /// section size.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyDictionary<string, Message>> GetSourceMessagesAsync(
        IMessageRepository messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return _sourceMessages ??= FetchSourceMessagesAsync(messages, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, Message>> FetchSourceMessagesAsync(
        IMessageRepository messages, CancellationToken cancellationToken)
    {
        var ids = Facts.SelectMany(fact => fact.SourceMessageIds)
            .Concat(Entities.SelectMany(entity => entity.SourceMessageIds))
            .Concat(Preferences.SelectMany(preference => preference.SourceMessageIds))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, Message>(StringComparer.Ordinal);

        var fetched = await messages.GetByIdsAsync(ids, cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, Message>(StringComparer.Ordinal);
        foreach (var message in fetched)
            map[message.MessageId] = message;

        return map;
    }

    /// <summary>Merges a contribution into one item's annotation, preserving what other features wrote.</summary>
    public void Annotate(string itemId, Func<ProjectedItemAnnotation, ProjectedItemAnnotation> contribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(contribute);

        _annotations[itemId] = contribute(
            _annotations.TryGetValue(itemId, out var existing) ? existing : new ProjectedItemAnnotation());
    }

    public void AddBlock(ProjectedBlockKind kind, string sectionKey, string text) =>
        _blocks.Add(new ProjectedBlock(kind, sectionKey, text));

    public void SetSectionOrder(string sectionKey, IReadOnlyList<string> orderedIds) =>
        _sectionOrder[sectionKey] = orderedIds;

    /// <summary>True when no feature contributed anything at all.</summary>
    public bool IsEmpty => _annotations.Count == 0 && _blocks.Count == 0 && _sectionOrder.Count == 0;

    public ProjectedContext Build() => new()
    {
        Annotations = _annotations,
        Blocks = _blocks,
        SectionOrder = _sectionOrder,
    };
}

/// <summary>The section keys projection and every render surface agree on.</summary>
/// <remarks>
/// Constants rather than literals because three surfaces and five features index by these strings; a
/// typo in any one of them would silently drop that section's projection with nothing to notice.
/// </remarks>
internal static class ProjectionSectionKeys
{
    public const string Entities = "entities";
    public const string Facts = "facts";
    public const string Preferences = "preferences";
    public const string Traces = "traces";
}
