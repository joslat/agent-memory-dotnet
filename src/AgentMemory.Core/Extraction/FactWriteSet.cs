using static AgentMemory.Core.Extraction.FactNames;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction;

/// <summary>Tracks this operation's current-value decision and the facts actually written.</summary>
internal sealed class FactWriteSet
{
    public FactNames Names { get; }
    public DateTimeOffset Now { get; }
    public Dictionary<string, string> FactCorrections { get; }
    public CurrentValues.Decision Decision { get; }
    public IReadOnlyDictionary<string, string> Replaced => Decision.Replaced;
    public Dictionary<string, int> FactIndex { get; }
    public Dictionary<string, Fact> ByKey { get; }
    public List<(string Key, Fact Fact)> WrittenFacts { get; }
    public HashSet<string> CurrentIds { get; }
    public Dictionary<string, ExtractedFact> ExtractedByKey { get; }
    public HashSet<string> CreatedIds { get; }

    public FactWriteSet(IReadOnlyList<PreparedFact> facts, FactNames names, ExtractionOptions options,
        bool writtenByWriter, IClock clock)
    {
        Names = names;
        // 36.4. What each marked correction replaces, by the words it was extracted as (the source key every
        // outcome of this persist is keyed by). Empty unless the extractor was asked to mark corrections. A correction
        // naming its own value marks nothing: the extractor repeated the value, and the fact is an ordinary statement.
        FactCorrections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var preparedFact in facts)
        {
            var item = preparedFact.Item;
            if (string.IsNullOrWhiteSpace(item.Replaces)) continue;
            var value = Corrections.Value(Names.CanonicalName(item.Replaces!));
            if (value.Length == 0 || value == Corrections.Value(Names.StoredName(item, subject: false))) continue;
            FactCorrections.TryAdd(FactSourceKey(item), item.Replaces!);
        }
        // Which value this extraction leaves current (CurrentValues), in stored terms: the subject as stored (the self
        // words one speaker), the value as stored. A replaced fact supersedes nothing; its current value closes it once
        // every fact is written, on either write path.
        // A value that has ended, or not begun, states no current value ("lived in Berlin until 2019", "moving to Oslo
        // next month") and joins no group: one rule (ReplacementShapes.HoldsNow) with the supersession query's.
        Now = clock.UtcNow;
        Decision = !(options.SupersedeReplacedFacts && !writtenByWriter)
            ? new CurrentValues.Decision(new Dictionary<string, string>(), new HashSet<string>())
            : CurrentValues.Decide(
                facts.Select(f => f.Item).ToList(),
                FactSourceKey,
                FactRelationKey,
                (fact, correction) =>
                    FactCorrections.TryGetValue(FactSourceKey(correction), out var named) &&
                    FactSubjectKey(fact) == FactSubjectKey(correction) &&
                    MemoryRelationCardinality.ReplacedKeys(correction.Predicate)
                        .Contains(MemoryTripleCanonicalizer.Canonical(fact.Predicate), StringComparer.Ordinal) &&
                    Corrections.Value(Names.StoredName(fact, subject: false)) == Corrections.Value(Names.CanonicalName(named)),
                fact => fact.ValidFrom ?? fact.OccurredOn ?? Now);
        // The order the facts were said, by source key, and each written fact by its key.
        FactIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (preparedFact, index) in facts.Select((f, i) => (f, i)))
            FactIndex.TryAdd(FactSourceKey(preparedFact.Item), index);
        ByKey = new Dictionary<string, Fact>(StringComparer.Ordinal);
        // Every fact written, in the order recorded: nothing is closed until all of them are (review round 8: the item
        // path closed while writing, so it saw a value not yet restated as absent, and the paths disagreed).
        WrittenFacts = new List<(string Key, Fact Fact)>();
        // The current value of each single-valued relation this extraction states: no correction of the same extraction
        // closes it ("Copenhagen, not Oslo ... no, Oslo, not Copenhagen" leaves Oslo).
        CurrentIds = new HashSet<string>(StringComparer.Ordinal);
        ExtractedByKey = new Dictionary<string, ExtractedFact>(StringComparer.Ordinal);
        foreach (var preparedFact in facts)
            ExtractedByKey.TryAdd(FactSourceKey(preparedFact.Item), preparedFact.Item);
        // The facts this extraction CREATED: a correction's other-relation fallback leaves them alone ("Oslo now, not
        // Copenhagen; Copenhagen is still my favourite city"). A stored fact merely restated is not among them: the
        // fallback exists for what was stored before.
        CreatedIds = new HashSet<string>(StringComparer.Ordinal);
    }

    private string FactSubjectKey(ExtractedFact fact)
    {
        var stored = Names.StoredName(fact, subject: true);
        return MemoryUserNames.IsSelf(stored) ? "\u0001self" : MemoryTripleCanonicalizer.CanonicalValue(stored);
    }

    private string? FactRelationKey(ExtractedFact fact) =>
        ReplacementShapes.HoldsNow(fact.ValidFrom ?? fact.OccurredOn, fact.ValidUntil, Now) &&
        MemoryRelationCardinality.Relation(fact.Predicate) is { } relation
            ? FactSubjectKey(fact) + "\u0001" + relation
            : null;

    public void RecordWritten(string sourceKey, ExtractedFact? extracted, Fact persisted)
    {
        ByKey.TryAdd(sourceKey, persisted);
        if (!Replaced.ContainsKey(sourceKey) && extracted is not null && FactRelationKey(extracted) is not null)
            CurrentIds.Add(persisted.FactId);
    }
}
