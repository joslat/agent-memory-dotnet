using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Services;

internal sealed partial class MemoryContextAssembler
{
    /// <summary>Person details: the caller's explicit choice, else on where evidence-based identity runs.</summary>
    private bool PersonDetailsOn(RecallOptions options) => _identity?.PersonDetails(options.PersonDetails) ?? options.PersonDetails == true;

    /// <summary>
    /// ILC-33: the facts bound to the confirmed people the question names, by a unique name only. Two bounded reads: the
    /// owner's confirmed people, then, only when the question names one of them, that person's facts.
    /// </summary>
    private async Task<IReadOnlyList<Fact>> ExpandPersonDetailsAsync(IReadOnlyList<Fact> facts,
        LookAheadResult lookAhead, ProspectiveDueResult due, string question, RecallOptions options, MemoryScope scope,
        CancellationToken cancellationToken, TimeSpan? remaining)
    {
        if (!PersonDetailsOn(options) || options.MaxFacts <= 0 || options.MaxPersonDetailFacts == 0 || string.IsNullOrWhiteSpace(question))
            return facts;
        if (options.MaxPersonDetailFacts is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Person detail cap must be between 0 and 64.");
        if (remaining is { } left && left <= TimeSpan.Zero) return facts;
        var seen = facts.Concat(lookAhead.Window).Concat(lookAhead.Upcoming).Concat(lookAhead.Overlapping)
            .Concat(due.Due).Concat(due.Expiring).Select(fact => fact.FactId).ToHashSet(StringComparer.Ordinal);
        using var expiry = remaining is not null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (expiry is not null) expiry.CancelAfter(remaining!.Value);
        var token = expiry?.Token ?? cancellationToken;
        IReadOnlyList<Fact> details;
        try
        {
            var people = await TimedAsync("memory.recall.person-identities", () => _longTerm.GetPersonIdentitiesAsync(
                PersonMentions.MaxPeopleRead, scope, token)).ConfigureAwait(false);
            // A full page may leave a namesake past the bound, so no name is known to be unique: expand nobody.
            if (people.Count >= PersonMentions.MaxPeopleRead) return facts;
            var named = PersonMentions.Named(question,
                people.Select(person => new PersonMentions.Person(person.EntityId, person.Surfaces, person.Confirmed)).ToArray());
            if (named.Count == 0) return facts;
            details = await TimedAsync("memory.recall.person-details", () => _longTerm.GetPersonDetailsAsync(
                named, seen.Take(512).ToArray(), options.MaxPersonDetailFacts, scope, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (expiry is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            return facts;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var widened = new List<Fact>(facts);
        foreach (var detail in details.Take(options.MaxPersonDetailFacts))
            if (seen.Add(detail.FactId)) widened.Add(detail);
        return widened;
    }
}
