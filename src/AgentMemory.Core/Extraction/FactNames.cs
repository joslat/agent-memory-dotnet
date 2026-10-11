using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Extraction;

/// <summary>Uses the same resolved names for fact preparation, correction matching and batch identity.</summary>
internal sealed class FactNames
{
    private readonly ExtractionOptions _options;
    private readonly IReadOnlyDictionary<string, Entity> _entities;
    private readonly string? _userName;

    public FactNames(ExtractionOptions options, IReadOnlyDictionary<string, Entity> entities, string? userName)
    {
        _options = options;
        _entities = entities;
        _userName = userName;
    }

    // I-2. The name a subject or object is stored under: the resolved entity's, when it resolved to one.
    public string CanonicalName(string surface) =>
        _options.CanonicalFactSubjects && !string.IsNullOrWhiteSpace(surface) &&
        _entities.TryGetValue(surface, out var resolved) && !string.IsNullOrWhiteSpace(resolved.Name)
            ? resolved.Name
            : surface;

    // The one place a stored subject or object is decided: the fact's preparation and the batch
    // distinctness guard both call it, so they cannot disagree about which facts become one node.
    public string StoredName(ExtractedFact fact, bool subject)
    {
        var surface = subject ? fact.Subject : fact.Object;
        if (_userName is not null && MemoryUserNames.MeansUser(fact, subject)) return CanonicalName(_userName);
        // The words that mean the user are never renamed to an entity's name: found live, "user" resolved into
        // the person "Dana" and canonical subjects stored the naming fact as "Dana | is named | Dana", so the
        // name could never be found again (it is looked up under "user"). Only the rule above renames them.
        if (subject && MemoryUserNames.IsSelf(surface)) return surface;
        return CanonicalName(surface);
    }

    // The source key stays the words as extracted: outcomes are keyed by the input item.
    internal static string FactSourceKey(ExtractedFact fact) => $"{fact.Subject} {fact.Predicate} {fact.Object}";
}
