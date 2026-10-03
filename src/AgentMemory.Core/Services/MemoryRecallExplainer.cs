using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Options;

namespace AgentMemory.Core.Services;

/// <summary>
/// G5 (PLAN 40.49): the gates a fact passes on its way into a recall, checked in the order recall applies them: owner,
/// router, closing, valid time, similarity, then rank and budget, read from the recall itself.
/// </summary>
internal sealed class MemoryRecallExplainer(
    IMemoryService memory,
    IFactRepository facts,
    IEmbeddingOrchestrator embeddings,
    IOptions<MemoryOptions> options,
    IClock clock,
    IMemoryRouter? router = null,
    IMemoryHistoryService? history = null) : IMemoryRecallExplainer
{
    public async Task<MemoryWhyNot> WhyNotFactAsync(RecallRequest request, string factId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MemoryWhyNot Gate(string gate, string detail, double? score = null, double? floor = null, string? related = null) =>
            new() { ItemId = factId, Gate = gate, Detail = detail, Score = score, Floor = floor, RelatedId = related };

        var fact = await facts.GetByIdAsync(factId, cancellationToken).ConfigureAwait(false);
        if (fact is null) return Gate(MemoryWhyNot.NotFound, "No fact has this id.");
        if (request.UserId is { } asker && fact.OwnerId is { } owner && !string.Equals(owner, asker, StringComparison.Ordinal))
            return Gate(MemoryWhyNot.Owner, "Another owner's memory: never recalled for this user.");

        var settings = options.Value;
        if (settings.Routing.Enabled && router is not null)
        {
            var route = router.Route(request.Question ?? request.Query);
            if (!route.Recall) return Gate(MemoryWhyNot.Router, $"The router read no memory for this question ({route.SkipReason}).");
            if (!route.Kinds.Contains(MemoryRoute.Facts)) return Gate(MemoryWhyNot.Router, "The router did not read facts for this question.");
        }

        if (fact.InvalidatedAtUtc is { } closedAt)
        {
            var when = closedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (fact.InvalidatedReason == "decay") return Gate(MemoryWhyNot.Decayed, $"Let go by decay on {when}.");
            // A closing by a change or a correction names its successor; a closing without a reason (before 40.65) is told
            // apart from a plain retraction by having one.
            var successor = history is null ? null
                : (await history.GetHistoryAsync(new MemoryHistoryQuery { Kind = MemoryHistoryKind.Fact, Id = factId, Limit = 1 }, cancellationToken)
                    .ConfigureAwait(false)).FirstOrDefault()?.SupersededByIds.FirstOrDefault();
            if (successor is null && fact.InvalidatedReason is not ("change" or "correction"))
                return Gate(MemoryWhyNot.Invalidated, $"Invalidated on {when}.");
            var how = fact.InvalidatedReason is { } why ? $" as a {why}" : "";
            return Gate(MemoryWhyNot.Closed, $"Closed{how} on {when}{(successor is null ? "" : "; replaced by another fact")}.", related: successor);
        }

        var recall = MemoryContextAssembler.EffectiveRecall(request.Options, settings.Recall);
        var now = request.TemporalReferenceTime ?? clock.UtcNow;
        if (recall.ValidTime == ValidTimeMode.Current &&
            ((fact.ValidFrom is { } from && from > now) || (fact.ValidUntil is { } until && until <= now)))
            return Gate(MemoryWhyNot.Validity, "Outside its valid time at the recall's instant.");

        double? score = null;
        if (fact.Embedding is { Length: > 0 } stored)
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
        var recalled = context.RelevantFacts.Items.Concat(context.DueFacts.Items).Concat(context.ExpiringFacts.Items);
        if (recalled.Any(f => f.FactId == factId))
            return Gate(MemoryWhyNot.Recalled, "It was recalled.", score, recall.MinSimilarityScore);
        if (context.Truncated)
            return Gate(MemoryWhyNot.Budget, "Above the floor, and cut to fit the context budget.", score, recall.MinSimilarityScore);
        return Gate(MemoryWhyNot.Rank,
            $"Above the floor, and outranked: the {recall.MaxFacts} fact slots went to memories that scored higher.",
            score, recall.MinSimilarityScore);
    }
}
