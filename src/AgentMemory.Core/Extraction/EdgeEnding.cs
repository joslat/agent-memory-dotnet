using static AgentMemory.Core.Extraction.FactClosureWriter;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Ends mirrored edges and withdrawn event dates after this extraction's relationships are written.</summary>
internal sealed class EdgeEnding
{
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IRelationshipRepository _relationshipRepository;
    private readonly IClock _clock;
    private readonly ExtractionOptions _options;
    private readonly ILogger _logger;
    private readonly FactClosureWriter _factClosures;

    public EdgeEnding(IEntityRepository entityRepository, IFactRepository factRepository,
        IRelationshipRepository relationshipRepository, IClock clock, ExtractionOptions options,
        ILogger logger, FactClosureWriter factClosures)
    {
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _relationshipRepository = relationshipRepository;
        _clock = clock;
        _options = options;
        _logger = logger;
        _factClosures = factClosures;
    }

    public async Task ApplyAsync(IReadOnlyList<WithdrawnFact> withdrawn, CancellationToken cancellationToken = default)
    {
        foreach (var item in withdrawn)
        {
            // 40.65. A change's edge ends when the change took effect, as its fact does; a correction's at once.
            DateTimeOffset? endAt = _options.BitemporalChanges && !item.Correction ? ChangedAt(item.Winner, _clock.UtcNow) : null;
            await EndMirroredEdgesAsync(item.Closed, item.StillLive, item.ReadScope, item.WriteScope, cancellationToken, endAt)
                .ConfigureAwait(false);
            if (item.Correction)
                await CloseDatesOfWithdrawnAsync(item.Closed, item.Winner, item.ReadScope, item.WriteScope, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 38.6 (the Conversational sample, 2026-10-01). A correction closed "Lena | is training for | half marathon", and the
    /// edge extracted from the same words ("Lena —is training for→ half marathon") stayed live: recall rendered it, and the
    /// agent answered from it. The fact and its edge are one statement kept in two places, so they end together: the live
    /// edge from the fact's subject, of the fact's relation, to an entity the fact names. Unless a fact still live (or the
    /// correction itself) says the same relation to that entity. Best-effort, like the edge ending of a single-valued relation.
    /// </summary>
    private async Task EndMirroredEdgesAsync(
        Fact closed, IReadOnlyList<Fact> stillLive, MemoryScope readScope, MemoryScope? writeScope, CancellationToken cancellationToken,
        DateTimeOffset? endAt = null)
    {
        try
        {
            var source = await SubjectEntityAsync(closed.Subject, readScope, cancellationToken).ConfigureAwait(false);
            if (source is null) return;
            var now = _clock.UtcNow;
            var edges = await _relationshipRepository.GetBySourceEntityAsync(source.EntityId, readScope, cancellationToken)
                .ConfigureAwait(false);
            foreach (var edge in edges.Where(edge => (edge.ValidUntil is not { } until || until > now) &&
                                                     SameRelation(edge.RelationshipType, closed.Predicate)))
            {
                var target = await _entityRepository.GetByIdAsync(edge.TargetEntityId, cancellationToken).ConfigureAwait(false);
                if (target is null || !Corrections.NamesEither(closed.Object, target.Name)) continue;
                if (stillLive.Any(fact => SameRelation(fact.Predicate, closed.Predicate) && Corrections.NamesEither(fact.Object, target.Name)))
                    continue;
                await _relationshipRepository.EndAsync(edge.RelationshipId, endAt is { } at && at <= now ? at : now, writeScope, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogDebug("Ended relationship '{Edge}' with the fact '{Fact}' it mirrors.", edge.RelationshipId, closed.FactId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NotSupportedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ending the edge beside closed fact '{Id}' failed; it stays live.", closed.FactId);
        }
    }

    /// <summary>
    /// 38.6 (the Conversational sample, 2026-10-01). "I'm training for the half marathon in April" was stored as the plan
    /// and, from the same words, as "half marathon | takes place in | 2027-04". The correction closed the plan; the date
    /// stayed, and the agent answered "with the half marathon in April as a milestone along the way". A withdrawn statement
    /// takes its own date with it: a live fact about the event it named, from the same message, whose value is only a date.
    /// Corrections only, never supersession ("Lyon is in France" outlives a move, and is not a date anyway). Review: only
    /// a schedule ("takes place in", "is on"), never history ("first held in 1998"); and not while someone else still has
    /// a live edge to the event (the brother still runs it in April).
    /// </summary>
    private async Task CloseDatesOfWithdrawnAsync(
        Fact closed, Fact winner, MemoryScope readScope, MemoryScope? writeScope, CancellationToken cancellationToken)
    {
        if (closed.SourceMessageIds.Count == 0) return;
        var said = Corrections.Undated(closed.Object);
        if (said.Length == 0) return;
        try
        {
            var facts = new List<Fact>();
            foreach (var subject in new[] { said, said.ToLowerInvariant() }.Distinct(StringComparer.Ordinal))
                facts.AddRange(await _factRepository.GetBySubjectAsync(subject, readScope, cancellationToken).ConfigureAwait(false));
            var dates = facts.DistinctBy(fact => fact.FactId).Where(fact =>
                fact.InvalidatedAtUtc is null && fact.FactId != closed.FactId && fact.FactId != winner.FactId &&
                fact.SourceMessageIds.Intersect(closed.SourceMessageIds, StringComparer.Ordinal).Any() &&
                IsSchedule(fact.Predicate) && Corrections.IsDateOnly(fact.Object)).ToList();
            if (dates.Count == 0) return;
            var theEvent = await _entityRepository.FindLiveByNameAsync(said, null, readScope, cancellationToken).ConfigureAwait(false);
            if (theEvent is not null)
            {
                var subject = await SubjectEntityAsync(closed.Subject, readScope, cancellationToken).ConfigureAwait(false);
                var now = _clock.UtcNow;
                var goingStill = await _relationshipRepository.GetByTargetEntityAsync(theEvent.EntityId, readScope, cancellationToken)
                    .ConfigureAwait(false);
                if (goingStill.Any(edge => (edge.ValidUntil is not { } until || until > now) && edge.SourceEntityId != subject?.EntityId))
                    return;
            }
            foreach (var date in dates)
            {
                await _factClosures.CloseAsync(date.FactId, winner.FactId, FactClosureReason.Correction, null, writeScope, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogDebug("Closed '{Date}', the date of withdrawn fact '{Fact}'.", date.FactId, closed.FactId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the date of withdrawn fact '{Id}' failed; it stays live.", closed.FactId);
        }
    }

    /// <summary>
    /// The entity a fact's subject names. "user" is the person who named themselves ("user | is named | Lena"): without
    /// an owner the facts keep "user" while the person's edges hang off "Lena" (38.6 review).
    /// </summary>
    private async Task<Entity?> SubjectEntityAsync(string subject, MemoryScope readScope, CancellationToken cancellationToken)
    {
        var named = await _entityRepository.FindLiveByNameAsync(subject, null, readScope, cancellationToken).ConfigureAwait(false);
        if (named is not null || !MemoryUserNames.IsSelf(subject)) return named;
        var name = await _factRepository.FindLatestObjectAsync(MemoryUserNames.SelfWords, MemoryUserNames.NamingPredicates, readScope, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(name)
            ? null
            : await _entityRepository.FindLiveByNameAsync(name.Trim(), "PERSON", readScope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a predicate schedules an event ("takes place in", "is on"), as opposed to telling its history.</summary>
    private static bool IsSchedule(string predicate)
    {
        var words = MemoryTripleCanonicalizer.CanonicalValue(predicate);
        return words.StartsWith("takes place", StringComparison.Ordinal) || words.StartsWith("is on", StringComparison.Ordinal) ||
               words.StartsWith("is in", StringComparison.Ordinal) || words.StartsWith("is at", StringComparison.Ordinal) ||
               words.StartsWith("is scheduled", StringComparison.Ordinal) || words.StartsWith("scheduled", StringComparison.Ordinal) ||
               words.StartsWith("is held", StringComparison.Ordinal) || words.StartsWith("will be", StringComparison.Ordinal) ||
               words.StartsWith("happens", StringComparison.Ordinal) || words.StartsWith("starts", StringComparison.Ordinal) ||
               words.StartsWith("begins", StringComparison.Ordinal) || words is "date" or "on" or "in";
    }

    /// <summary>
    /// Whether an edge type and a predicate are one relation: the same words without a leading "is/am/are" (the sample
    /// stored "is training for" as a fact and TRAINING_FOR as its edge), or two stored forms of one relation ("works for",
    /// "works at"). A single-valued relation is one only in its present forms (36.4): "worked at" is history and neither
    /// ends nor is ended with "works at".
    /// </summary>
    private static bool SameRelation(string a, string b)
    {
        var present = MemoryRelationCardinality.Relation(a);
        var other = MemoryRelationCardinality.Relation(b);
        if (present is not null || other is not null) return present == other;
        var x = Bare(a);
        var y = Bare(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        var resolved = MemoryRelationLexicon.Default.ResolveStored(x);
        return resolved is not null && resolved == MemoryRelationLexicon.Default.ResolveStored(y);

        static string Bare(string predicate)
        {
            var canonical = MemoryTripleCanonicalizer.Canonical(predicate);
            foreach (var auxiliary in new[] { "is ", "am ", "are " })
            {
                if (canonical.StartsWith(auxiliary, StringComparison.Ordinal) && canonical.Length > auxiliary.Length)
                    return canonical[auxiliary.Length..];
            }
            return canonical;
        }
    }
}

/// <summary>What a fact that closed also said elsewhere, ended once this extraction's relationships are written.</summary>
internal sealed record WithdrawnFact(
    Fact Closed, Fact Winner, IReadOnlyList<Fact> StillLive, MemoryScope ReadScope, MemoryScope? WriteScope, bool Correction);
