using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Options;

namespace AgentMemory.Core.Services;

/// <summary>
/// G5 (PLAN 40.49): the gates a memory passes on its way into a recall, checked in the order recall applies them: owner,
/// router, closing (or a merge), valid time (facts), similarity, then rank and budget, read from the recall itself. Facts,
/// entities and preferences, one path: what differs is how each is read and which section it lands in.
/// </summary>
internal sealed class MemoryRecallExplainer(
    IMemoryService memory,
    IFactRepository facts,
    IEmbeddingOrchestrator embeddings,
    IOptions<MemoryOptions> options,
    IClock clock,
    IMemoryRouter? router = null,
    IMemoryHistoryService? history = null,
    IEntityRepository? entities = null,
    IPreferenceRepository? preferences = null) : IMemoryRecallExplainer
{
    /// <summary>What the gates need of one memory, whatever its kind.</summary>
    private sealed record Candidate(
        string? OwnerId, DateTimeOffset? InvalidatedAt, string? InvalidatedReason, float[]? Embedding,
        DateTimeOffset? ValidFrom = null, DateTimeOffset? ValidUntil = null);

    public async Task<MemoryWhyNot> WhyNotAsync(
        RecallRequest request, MemoryItemKind kind, string itemId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MemoryWhyNot Gate(string gate, string detail, double? score = null, double? floor = null, string? related = null) =>
            new() { ItemId = itemId, Gate = gate, Detail = detail, Score = score, Floor = floor, RelatedId = related };

        var (routeKind, historyKind, noun) = kind switch
        {
            MemoryItemKind.Fact => (MemoryRoute.Facts, MemoryHistoryKind.Fact, "fact"),
            MemoryItemKind.Entity => (MemoryRoute.Graph, MemoryHistoryKind.Entity, "entity"),
            MemoryItemKind.Preference => (MemoryRoute.Preferences, MemoryHistoryKind.Preference, "preference"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Why-not explains facts, entities and preferences."),
        };

        // Entities carry no closing on their record: the history row does (and says what a merged entity lives on in).
        var row = history is null ? null
            : (await history.GetHistoryAsync(new MemoryHistoryQuery { Kind = historyKind, Id = itemId, Limit = 1 }, cancellationToken)
                .ConfigureAwait(false)).FirstOrDefault();
        var candidate = kind switch
        {
            MemoryItemKind.Fact => await facts.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false) is { } f
                ? new Candidate(f.OwnerId, f.InvalidatedAtUtc, f.InvalidatedReason, f.Embedding, f.ValidFrom, f.ValidUntil) : null,
            MemoryItemKind.Preference => preferences is null ? null
                : await preferences.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false) is { } p
                    ? new Candidate(p.OwnerId, p.InvalidatedAtUtc, row?.ClosedAs, p.Embedding) : null,
            _ => entities is null ? null
                : await entities.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false) is { } e
                    ? new Candidate(e.OwnerId, row?.InvalidatedAtUtc, row?.ClosedAs, e.Embedding) : null,
        };
        if (candidate is null) return Gate(MemoryWhyNot.NotFound, $"No {noun} has this id.");
        if (request.UserId is { } asker && candidate.OwnerId is { } owner && !string.Equals(owner, asker, StringComparison.Ordinal))
            return Gate(MemoryWhyNot.Owner, "Another owner's memory: never recalled for this user.");

        var settings = options.Value;
        if (settings.Routing.Enabled && router is not null)
        {
            var route = router.Route(request.Question ?? request.Query);
            if (!route.Recall) return Gate(MemoryWhyNot.Router, $"The router read no memory for this question ({route.SkipReason}).");
            if (!route.Kinds.Contains(routeKind)) return Gate(MemoryWhyNot.Router, $"The router did not read {routeKind} for this question.");
        }

        if (row?.MergedIntoId is { } survivor)
            return Gate(MemoryWhyNot.Merged, "Merged into another entity: recall finds that one.", related: survivor);
        if (candidate.InvalidatedAt is { } closedAt)
        {
            var when = closedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (candidate.InvalidatedReason == "decay") return Gate(MemoryWhyNot.Decayed, $"Let go by decay on {when}.");
            // A closing by a change or a correction names its successor; a closing without a reason (before 40.65) is told
            // apart from a plain retraction by having one.
            var successor = row?.SupersededByIds.FirstOrDefault();
            if (successor is null && candidate.InvalidatedReason is not ("change" or "correction"))
                return Gate(MemoryWhyNot.Invalidated, $"Invalidated on {when}.");
            var how = candidate.InvalidatedReason is { } why ? $" as a {why}" : "";
            return Gate(MemoryWhyNot.Closed, $"Closed{how} on {when}{(successor is null ? "" : $"; replaced by another {noun}")}.", related: successor);
        }

        var recall = MemoryContextAssembler.EffectiveRecall(request.Options, settings.Recall);
        var now = request.TemporalReferenceTime ?? clock.UtcNow;
        if (recall.ValidTime == ValidTimeMode.Current &&
            ((candidate.ValidFrom is { } from && from > now) || (candidate.ValidUntil is { } until && until <= now)))
            return Gate(MemoryWhyNot.Validity, "Outside its valid time at the recall's instant.");

        if (candidate.Embedding is not { Length: > 0 })
            return Gate(MemoryWhyNot.NotEmbedded,
                "Has no embedding, so similarity search cannot find it; the embedding backfill (GenerateEmbeddingsBatchAsync) embeds it.");
        double? score = null;
        if (candidate.Embedding is { Length: > 0 } stored)
        {
            var query = request.QueryEmbedding ?? await embeddings.EmbedQueryAsync(request.Query, cancellationToken).ConfigureAwait(false);
            if (query is { Length: > 0 } && query.Length == stored.Length)
            {
                // The store's scale: Neo4j's cosine similarity is normalised to (1 + cos) / 2, and the floor is compared on it.
                score = (1 + Resolution.SemanticMatchEntityMatcher.CosineSimilarity(query, stored)) / 2;
                if (score < recall.MinSimilarityScore)
                    return Gate(MemoryWhyNot.Similarity,
                        string.Create(CultureInfo.InvariantCulture, $"Scored {score:0.000}, below the floor of {recall.MinSimilarityScore:0.000}."),
                        score, recall.MinSimilarityScore);
            }
        }

        var context = (await memory.RecallAsync(request, cancellationToken).ConfigureAwait(false)).Context;
        var (recalled, cap) = kind switch
        {
            MemoryItemKind.Fact => (context.RelevantFacts.Items.Concat(context.DueFacts.Items).Concat(context.ExpiringFacts.Items)
                .Any(f => f.FactId == itemId), recall.MaxFacts),
            MemoryItemKind.Preference => (context.RelevantPreferences.Items.Any(p => p.PreferenceId == itemId), recall.MaxPreferences),
            _ => (context.RelevantEntities.Items.Any(e => e.EntityId == itemId), recall.MaxEntities),
        };
        if (recalled) return Gate(MemoryWhyNot.Recalled, "It was recalled.", score, recall.MinSimilarityScore);
        if (context.Truncated)
            return Gate(MemoryWhyNot.Budget, "Above the floor, and cut to fit the context budget.", score, recall.MinSimilarityScore);
        return Gate(MemoryWhyNot.Rank,
            $"Above the floor, and outranked: the {cap} {noun} slots went to memories that scored higher.",
            score, recall.MinSimilarityScore);
    }
}
