using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Gate;

/// <summary>
/// Every memory a recall found, as the judge is shown it (its type, its text with dates), keyed so the decision can be
/// applied back to the sections; and the context keeping only the memories chosen.
/// </summary>
internal static class GateCandidates
{
    /// <summary>A candidate and the id of the item it came from (an id may appear in two sections: due and relevant).</summary>
    public sealed record Entry(MemoryGateCandidate Candidate, string ItemId);

    public static IReadOnlyList<Entry> Of(MemoryContext context)
    {
        var entries = new List<Entry>();
        void Add(string type, string id, string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                entries.Add(new Entry(new MemoryGateCandidate($"c{entries.Count + 1}", type, text), id));
        }
        foreach (var fact in context.RelevantFacts.Items) Add("semantic", fact.FactId, GatePrompt.Describe(fact));
        foreach (var fact in context.DueFacts.Items) Add("prospective", fact.FactId, GatePrompt.Describe(fact, "due now"));
        foreach (var fact in context.ExpiringFacts.Items) Add("prospective", fact.FactId, GatePrompt.Describe(fact, "expires soon"));
        foreach (var entity in context.RelevantEntities.Items) Add("entity-graph", entity.EntityId, $"{entity.Name} ({entity.Type})");
        foreach (var r in context.RelevantRelationships.Items)
            Add("entity-graph", r.Relationship.RelationshipId, $"{r.SourceName} -[{r.Relationship.RelationshipType}]-> {r.TargetName}");
        foreach (var p in context.RelevantPreferences.Items) Add("preference", p.PreferenceId, p.PreferenceText);
        foreach (var m in context.RelevantMessages.Items) Add("episodic", m.MessageId, $"{m.Role}: {m.Content}");
        foreach (var t in context.SimilarTraces.Items)
            Add("reasoning", t.TraceId, t.Outcome is { Length: > 0 } outcome ? $"{t.Task} ({outcome})" : t.Task);
        return entries;
    }

    /// <summary>The context with only <paramref name="kept"/> in the gated sections; everything else as it was.</summary>
    public static MemoryContext Keep(MemoryContext context, IReadOnlySet<string> kept) => context with
    {
        RelevantFacts = Filter(context.RelevantFacts, f => f.FactId, kept),
        DueFacts = Filter(context.DueFacts, f => f.FactId, kept),
        ExpiringFacts = Filter(context.ExpiringFacts, f => f.FactId, kept),
        RelevantEntities = Filter(context.RelevantEntities, e => e.EntityId, kept),
        RelevantRelationships = Filter(context.RelevantRelationships, r => r.Relationship.RelationshipId, kept),
        RelevantPreferences = Filter(context.RelevantPreferences, p => p.PreferenceId, kept),
        RelevantMessages = Filter(context.RelevantMessages, m => m.MessageId, kept),
        SimilarTraces = Filter(context.SimilarTraces, t => t.TraceId, kept),
    };

    private static MemoryContextSection<T> Filter<T>(MemoryContextSection<T> section, Func<T, string> id, IReadOnlySet<string> kept) =>
        section with
        {
            Items = [.. section.Items.Where(item => kept.Contains(id(item)))],
            RankedItems = [.. section.RankedItems.Where(r => kept.Contains(r.ItemId))],
        };
}
