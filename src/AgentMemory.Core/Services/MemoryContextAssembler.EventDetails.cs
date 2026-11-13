using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services;

internal sealed partial class MemoryContextAssembler
{
    /// <summary>Event details: the caller's explicit choice, else on where evidence-based identity runs.</summary>
    private bool EventDetailsOn(RecallOptions options) => _identity?.EventDetails(options.EventDetails) ?? options.EventDetails == true;

    private async Task<IReadOnlyList<Fact>> ExpandEventDetailsAsync(IReadOnlyList<Fact> facts,
        LookAheadResult lookAhead, ProspectiveDueResult due, RecallOptions options, MemoryScope scope,
        CancellationToken cancellationToken, TimeSpan? remaining)
    {
        if (!EventDetailsOn(options) || options.MaxFacts <= 0 || options.MaxEventDetailFacts == 0) return facts;
        if (options.MaxEventDetailFacts is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Event detail cap must be between 0 and 64.");
        if (remaining is { } left && left <= TimeSpan.Zero) return facts;
        var present = facts.Concat(lookAhead.Window).Concat(lookAhead.Upcoming).Concat(lookAhead.Overlapping)
            .Concat(due.Due).Concat(due.Expiring).ToArray();
        var now = _clock.GetUtcNow();
        // A question's anchor/window must not be crowded out by unrelated dated vector hits.
        var candidates = lookAhead.Window.Concat(lookAhead.Upcoming).Concat(lookAhead.Overlapping)
            .Concat(due.Due).Concat(facts).Concat(due.Expiring);
        var anchorId = lookAhead.ResolvedWindow?.AnchorFactId;
        var seeds = candidates.Where(fact => WindowOf(fact, now) is not null)
            .OrderBy(fact => fact.FactId == anchorId ? 0 : 1)
            .Select(fact => fact.FactId).Distinct(StringComparer.Ordinal).Take(10).ToArray();
        if (seeds.Length == 0) return facts;
        var seen = present.Select(fact => fact.FactId).ToHashSet(StringComparer.Ordinal);
        using var expiry = remaining is not null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (expiry is not null) expiry.CancelAfter(remaining!.Value);
        IReadOnlyList<Fact> details;
        try
        {
            details = await TimedAsync("memory.recall.event-details", () => _longTerm.GetEventDetailsAsync(
                seeds, seen.Take(512).ToArray(), options.MaxEventDetailFacts, scope, expiry?.Token ?? cancellationToken))
                .ConfigureAwait(false);
            (expiry?.Token ?? cancellationToken).ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (expiry is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            return facts;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var widened = new List<Fact>(facts);
        foreach (var detail in details.Take(options.MaxEventDetailFacts))
            if (seen.Add(detail.FactId)) widened.Add(detail);
        return widened;
    }
}
