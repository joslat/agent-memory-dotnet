using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// RECALL-NAMESAKE-LEAK (<see cref="Abstractions.Options.RecallOptions.NamesakeFilter"/>): keeps a namesake's facts and messages
/// out of recall. The recalled items' entities are grouped by a shared name word; in a group, the one the question means is
/// the single member known by a relation it names ("my dog") or the single member with a spelling it uses ("Vale"). A
/// member known as something else ("a cat") is a namesake, and an item about it and about no kept member is dropped. A
/// member with no known role stays: it may be the meant one split in two, and a split costs recall where a wrong drop costs
/// the answer. Deterministic; no model call.
/// </summary>
internal static class NamesakeFilter
{
    private static readonly Regex Word = new(@"[\p{L}\p{M}][\p{L}\p{M}'’-]*", RegexOptions.CultureInvariant);

    /// <summary>The item ids to drop, and the entities they belong to, for the diagnostics.</summary>
    public static (IReadOnlySet<string> DropItems, IReadOnlyList<string> DroppedEntities) Decide(
        string question, IReadOnlyList<RecalledItemEntity> links)
    {
        var none = ((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal), (IReadOnlyList<string>)[]);
        if (string.IsNullOrWhiteSpace(question) || links.Count == 0) return none;
        var entities = links.GroupBy(l => l.Entity.EntityId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (entities.Count < 2) return none;

        // Each entity's spellings (whole, and words of three letters or more) and what it is known as.
        var spellings = entities.ToDictionary(e => e.Entity.EntityId, e => Spellings(e), StringComparer.Ordinal);
        var known = entities.ToDictionary(e => e.Entity.EntityId, e => Known(e), StringComparer.Ordinal);

        // Namesake groups: entities that share a name word, joined transitively.
        var parent = entities.ToDictionary(e => e.Entity.EntityId, e => e.Entity.EntityId, StringComparer.Ordinal);
        string Find(string id) { while (parent[id] != id) id = parent[id] = parent[parent[id]]; return id; }
        var byWord = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in entities)
            foreach (var word in spellings[entity.Entity.EntityId].Words)
                if (byWord.TryGetValue(word, out var other)) parent[Find(entity.Entity.EntityId)] = Find(other);
                else byWord[word] = entity.Entity.EntityId;
        var groups = entities.GroupBy(e => Find(e.Entity.EntityId), StringComparer.Ordinal)
            .Select(g => g.Select(e => e.Entity.EntityId).ToList()).Where(g => g.Count >= 2).ToList();
        if (groups.Count == 0) return none;

        var asked = IdentityRelationCue.InQuestion(question);
        var questionWords = Word.Matches(question).Select(m => MemoryTripleCanonicalizer.CanonicalValue(m.Value)).ToHashSet(StringComparer.Ordinal);
        var canonicalQuestion = " " + string.Join(' ', Word.Matches(question).Select(m => MemoryTripleCanonicalizer.CanonicalValue(m.Value))) + " ";
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            // The one the question means: known by a relation it names, else the one spelling it uses only that one has.
            var byRelation = asked.Count == 0 ? [] : group.Where(id => known[id].Overlaps(asked)).ToList();
            string? target = byRelation.Count == 1 ? byRelation[0] : null;
            if (target is null && byRelation.Count == 0)
            {
                var named = group.Where(id => spellings[id].Whole.Any(s => canonicalQuestion.Contains(" " + s + " ", StringComparison.Ordinal))
                        || spellings[id].Words.Any(w => questionWords.Contains(w) &&
                               group.All(other => other == id || !spellings[other].Words.Contains(w))))
                    .ToList();
                if (named.Count == 1) target = named[0];
            }
            if (target is null) continue;
            var meant = known[target].Union(asked).ToHashSet(StringComparer.Ordinal);
            foreach (var id in group)
                if (id != target && known[id].Count > 0 && meant.Count > 0 && !known[id].Overlaps(meant))
                    dropped.Add(id);
        }
        if (dropped.Count == 0) return none;

        // An item goes only when every entity it is about is a dropped namesake.
        var drop = links.GroupBy(l => l.ItemId, StringComparer.Ordinal)
            .Where(g => g.All(l => dropped.Contains(l.Entity.EntityId)))
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return (drop, dropped.Order(StringComparer.Ordinal).ToList());
    }

    private sealed record Spelled(IReadOnlySet<string> Whole, IReadOnlySet<string> Words);

    private static Spelled Spellings(RecalledItemEntity e)
    {
        var all = new[] { e.Entity.Name, e.Entity.CanonicalName }.Concat(e.Entity.Aliases).Concat(e.KeptNames)
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var whole = all.Select(s => string.Join(' ', Word.Matches(s!).Select(m => MemoryTripleCanonicalizer.CanonicalValue(m.Value))))
            .Where(s => s.Length >= 3).ToHashSet(StringComparer.Ordinal);
        var words = all.SelectMany(s => Word.Matches(s!).Select(m => MemoryTripleCanonicalizer.CanonicalValue(m.Value)))
            .Where(w => w.Length >= 3).ToHashSet(StringComparer.Ordinal);
        return new(whole, words);
    }

    private static IReadOnlySet<string> Known(RecalledItemEntity e)
    {
        var names = new[] { e.Entity.Name }.Concat(e.Entity.Aliases).Concat(e.KeptNames).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var found = new HashSet<string>(IdentityRelationCue.Known(e.Entity, e.KnownAs), StringComparer.Ordinal);
        foreach (var text in e.SourceTexts)
            found.UnionWith(IdentityRelationCue.InText(text, names));
        return found;
    }
}
