using System.Collections.Frozen;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// 36.4. Puts a change of mind into the shape write-time supersession can act on, before it is stored. Runs only
/// with <c>ExtractionOptions.SupersedeReplacedFacts</c>, the feature it serves.
/// </summary>
/// <remarks>
/// <para>
/// Supersession replaces a value of a single-valued relation. Two common ways of stating a new value never reached
/// one (found in simulated conversations, each with both values left live):
/// </para>
/// <list type="bullet">
/// <item><b>An age inside the object of a plain "is".</b> "Bruno is 6 years old" then "Bruno is 7 years old": the
/// predicate is <c>is</c>, which holds many values. Written as <c>Bruno | age | 7</c>, the declared single-valued
/// <c>age</c> replaces 6.</item>
/// <item><b>An event that changes a state.</b> "I moved to Copenhagen" was stored as its own relation beside
/// "lives in Hamburg". The vocabulary declares what an event entails (<c>moved to</c> entails <c>lives in</c>); the
/// entailed state is written beside the event, and it is that state which replaces the old residence.</item>
/// </list>
/// <para>
/// Both are declared in the vocabulary (the <c>age</c> relation, the <c>entails</c> field) rather than coded per
/// case, and both only add or reshape a fact the conversation stated: nothing is inferred beyond the words.
/// </para>
/// </remarks>
internal static partial class ReplacementShapes
{
    private static readonly FrozenDictionary<string, (string State, string? When, FrozenSet<string> From)> Entailments =
        RelationVocabularyDocument.Load().Canonical
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value.Entails))
            .ToFrozenDictionary(
                entry => MemoryTripleCanonicalizer.Canonical(entry.Key),
                entry => (entry.Value.Entails!, entry.Value.EntailsWhen,
                    entry.Value.EntailsFrom.Select(MemoryTripleCanonicalizer.Canonical).ToFrozenSet(StringComparer.Ordinal)),
                StringComparer.Ordinal);

    private static readonly FrozenSet<string> Copulas =
        new[] { "is", "'s", "is now", "turned", "has turned", "just turned" }.ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^(?:now\s+)?(?<n>\d{1,3})(?:\s*-?\s*(?:years?|yrs?)(?:\s*-?\s*old)?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgeObject();

    /// <summary>The facts to store: ages as <c>age</c>, and each event's entailed state beside it.</summary>
    /// <param name="facts">The extracted facts.</param>
    /// <param name="typeOf">The entity type the extraction resolved a name to, or null when it did not type it.</param>
    /// <param name="now">The write's clock: an event dated after it has not happened, and entails nothing yet.</param>
    internal static IReadOnlyList<ExtractedFact> Prepare(
        IReadOnlyList<ExtractedFact> facts, Func<string, string?> typeOf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(typeOf);
        var aged = facts.Select(AsAge).ToList();
        // The CURRENT states this extraction states outright, by subject and relation: "I moved to London in 2010; now
        // I live in Paris" states the residence, and an entailed "lives in London" must not be written over it. Only a
        // present form of a single-valued relation, still valid: "I lived in Paris, then moved to London" states
        // history, and London is the home.
        // The speaker by any name this extraction states for them ("I'm Dana"): "Dana lives in Paris" is the speaker's home.
        var names = aged.Where(PersistenceStage.UserNames.IsNamingFact)
            .Select(fact => MemoryTripleCanonicalizer.CanonicalValue(fact.Object))
            .ToHashSet(StringComparer.Ordinal);
        string SubjectKey(string subject) =>
            PersistenceStage.UserNames.IsSelf(subject) || names.Contains(MemoryTripleCanonicalizer.CanonicalValue(subject))
                ? "\u0001self"
                : MemoryTripleCanonicalizer.CanonicalValue(subject);
        var statedStates = aged
            .Where(fact => StillHolds(fact.ValidUntil, now))
            .Select(fact => (Subject: SubjectKey(fact.Subject), Relation: MemoryRelationCardinality.Relation(fact.Predicate)))
            .Where(pair => pair.Relation is not null)
            .ToHashSet();
        var entailedObjects = new HashSet<(string, string, string)>();
        var shaped = new List<ExtractedFact>(aged.Count + 1);
        foreach (var fact in aged)
        {
            shaped.Add(fact);
            var relation = MemoryRelationLexicon.Default.ResolveStored(fact.Predicate);
            if (relation is null || !Entailments.TryGetValue(relation, out var entailed)) continue;
            // Only a completed event entails ("moved to", not "moving to"), and only one that has happened.
            if (!entailed.From.Contains(MemoryTripleCanonicalizer.Canonical(fact.Predicate))) continue;
            if ((fact.ValidFrom ?? fact.OccurredOn) is { } from && from > now) continue;
            // "moved to the analytics team" is not a new home: the entailment holds for the declared kind of object.
            if (entailed.When is { } required &&
                !string.Equals(typeOf(fact.Object), required, StringComparison.OrdinalIgnoreCase)) continue;
            var subject = SubjectKey(fact.Subject);
            var state = MemoryTripleCanonicalizer.Canonical(entailed.State);
            if (statedStates.Contains((subject, state))) continue;
            // Two moves in one turn each entail their home, in order, so the later replaces the earlier; the same home
            // twice is written once.
            if (!entailedObjects.Add((subject, state, MemoryTripleCanonicalizer.CanonicalValue(fact.Object)))) continue;
            // Right after its event, so it is written in the order the conversation implies.
            // The state holds from the event's day ("moved to Oslo in March" lives there since March); it is no event.
            shaped.Add(fact.OccurredOn is { } movedOn && fact.ValidFrom is null
                ? fact with
                {
                    Predicate = entailed.State, ValidFrom = movedOn, ValidFromPrecision = fact.OccurredOnPrecision,
                    OccurredOn = null, OccurredOnPrecision = DatePrecision.Unspecified,
                }
                : fact with { Predicate = entailed.State, OccurredOn = null, OccurredOnPrecision = DatePrecision.Unspecified });
        }
        return shaped;
    }

    /// <summary>
    /// "X is 6 years old" / "X turned 7" as <c>X | age | 6</c>. A bare number after "is" is not an age ("Bruno is
    /// 7" could be anything); after "turned" it is.
    /// </summary>
    internal static ExtractedFact AsAge(ExtractedFact fact)
    {
        var predicate = MemoryTripleCanonicalizer.Canonical(fact.Predicate);
        if (!Copulas.Contains(predicate)) return fact;
        var match = AgeObject().Match(fact.Object.Trim());
        if (!match.Success) return fact;
        var namesYears = fact.Object.Contains("year", StringComparison.OrdinalIgnoreCase)
            || fact.Object.Contains("yr", StringComparison.OrdinalIgnoreCase);
        if (!namesYears && predicate is not ("turned" or "has turned" or "just turned")) return fact;
        return fact with { Predicate = "age", Object = match.Groups["n"].Value };
    }

    /// <summary>
    /// Whether a value still holds at <paramref name="now"/>: it states no end, or its end is still ahead. One rule for
    /// every place that asks: a value that has ended states history, never the current value, and replaces nothing.
    /// </summary>
    internal static bool StillHolds(DateTimeOffset? validUntil, DateTimeOffset now) => validUntil is not { } until || until > now;

    /// <summary>
    /// Whether a value holds at <paramref name="now"/>: it has begun (<paramref name="since"/> is not ahead) and still
    /// holds. The supersession query (<c>FactQueries.FindSupersededCandidates</c>) applies the same rule to losers.
    /// </summary>
    internal static bool HoldsNow(DateTimeOffset? since, DateTimeOffset? validUntil, DateTimeOffset now) =>
        (since is not { } from || from <= now) && StillHolds(validUntil, now);

    private static (string, string, string) Key(ExtractedFact fact) => (
        MemoryTripleCanonicalizer.CanonicalValue(fact.Subject),
        MemoryRelationLexicon.Default.ResolveStored(fact.Predicate) ?? MemoryTripleCanonicalizer.Canonical(fact.Predicate),
        MemoryTripleCanonicalizer.CanonicalValue(fact.Object));
}
