using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Services;

internal sealed partial class MemoryContextAssembler
{
    /// <summary>Person details: the caller's explicit choice, else on where evidence-based identity runs.</summary>
    private bool PersonDetailsOn(RecallOptions options) => _identity?.PersonDetails(options.PersonDetails) ?? options.PersonDetails == true;

    /// <summary>
    /// ILC-33: the facts bound to the confirmed people the question names, by a unique name only. Three bounded reads:
    /// every spelling people are known by; only when one is in the question, the owner's people with their names; only
    /// when the question names one of them, that person's facts. Also returns each named person's spellings, so the
    /// hand-over to current values treats them as one subject (REV2-5).
    /// </summary>
    private async Task<(IReadOnlyList<Fact> Facts, IReadOnlyDictionary<string, string> Subjects)> ExpandPersonDetailsAsync(
        IReadOnlyList<Fact> facts, LookAheadResult lookAhead, ProspectiveDueResult due, string question, RecallOptions options,
        MemoryScope scope, CancellationToken cancellationToken, TimeSpan? remaining)
    {
        var none = (facts, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal));
        if (!PersonDetailsOn(options) || options.MaxFacts <= 0 || options.MaxPersonDetailFacts == 0 || string.IsNullOrWhiteSpace(question))
            return none;
        if (options.MaxPersonDetailFacts is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Person detail cap must be between 0 and 64.");
        if (remaining is { } left && left <= TimeSpan.Zero) return none;
        var seen = facts.Concat(lookAhead.Window).Concat(lookAhead.Upcoming).Concat(lookAhead.Overlapping)
            .Concat(due.Due).Concat(due.Expiring).Select(fact => fact.FactId).ToHashSet(StringComparer.Ordinal);
        using var expiry = remaining is not null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (expiry is not null) expiry.CancelAfter(remaining!.Value);
        var token = expiry?.Token ?? cancellationToken;
        IReadOnlyList<Fact> details;
        IReadOnlyList<PersonIdentity> people;
        IReadOnlyList<PersonMentions.Person> known;
        IReadOnlyList<string> named;
        try
        {
            // REV2-2: first every spelling people are known by, a light read; the full read only when one of them is in
            // the question. A full page of spellings may leave one out, so it goes on to the full read: this step can
            // skip work, never a person.
            var spellings = await TimedAsync("memory.recall.person-names", () => _longTerm.GetPersonNamesAsync(
                PersonMentions.MaxSpellingsRead, scope, token)).ConfigureAwait(false);
            if (spellings.Count < PersonMentions.MaxSpellingsRead && !PersonMentions.Mentions(question, spellings)) return none;
            people = await TimedAsync("memory.recall.person-identities", () => _longTerm.GetPersonIdentitiesAsync(
                PersonMentions.MaxPeopleRead, scope, token)).ConfigureAwait(false);
            // A full page may leave a namesake past the bound, and a person whose names were cut off may carry any name, so
            // no name is known to be unique: expand nobody (PR260-F2, N4).
            if (people.Count >= PersonMentions.MaxPeopleRead || people.Any(person => !person.Complete)) return none;
            known = people.Select(person => new PersonMentions.Person(person.EntityId, person.Surfaces, person.Confirmed)).ToArray();
            named = PersonMentions.Named(question, known);
            if (named.Count == 0) return none;
            details = await TimedAsync("memory.recall.person-details", () => _longTerm.GetPersonDetailsAsync(
                named, seen.Take(512).ToArray(), options.MaxPersonDetailFacts, scope, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (expiry is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            return none;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var widened = new List<Fact>(facts);
        foreach (var detail in details.Take(options.MaxPersonDetailFacts))
            if (seen.Add(detail.FactId)) widened.Add(detail);
        // REV2-5: the spellings of a named person, so the hand-over to current values treats "Ana" and "Ana García" as one.
        // REV3-2: only spellings no other person has; a fact about a shared "Ana" stays its own subject.
        var subjects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in named)
            foreach (var surface in PersonMentions.OwnSpellings(known, id))
                subjects.TryAdd(MemoryTripleCanonicalizer.CanonicalValue(surface), id);
        return (widened, subjects);
    }
}
