using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Boosts candidates that sit close, in the graph, to the entity the query is about (R6).
/// </summary>
/// <remarks>
/// <para>
/// Vector similarity asks whether a row <i>looks like</i> the query. This asks whether it is
/// <i>about</i> what the query is about, and the two disagree in useful ways: a plainly-worded fact
/// hanging directly off the named entity is often the answer, while a fact that paraphrases the query
/// beautifully may concern someone else entirely.
/// </para>
/// <para>
/// <b>One decay constant, not two.</b> The boost is γ^hops using
/// <see cref="MemoryRankingOptions.EffectiveStructuralDecayGamma"/> — the same γ GraphRAG's hop decay
/// already uses. A second, independently-tuned constant for the same idea would drift from the first
/// and nobody would notice which one a given result came from.
/// </para>
/// <para>
/// <b>Cost is bounded to two queries per section</b>, regardless of candidate count: one to find the
/// centroid, one to measure distance to every candidate at once. This runs on the blocking recall
/// path, so a per-candidate traversal would make the reranker cost more than the retrieval it
/// reorders.
/// </para>
/// <para>
/// Off by default. It changes recall ordering, every recorded measurement was taken without it, and
/// the hermetic perf harness counts queries — so enabling it is a stated decision with a visible cost.
/// </para>
/// </remarks>
internal sealed class NodeDistanceReranker : IMemoryReranker
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly MemoryRankingOptions _ranking;
    private readonly MemoryOptions _memory;
    private readonly ILogger<NodeDistanceReranker> _logger;

    public NodeDistanceReranker(
        INeo4jTransactionRunner tx,
        ILogger<NodeDistanceReranker> logger,
        IOptions<MemoryRankingOptions>? ranking = null,
        IOptions<MemoryOptions>? memory = null)
    {
        _tx = tx;
        _logger = logger;
        _ranking = ranking?.Value ?? MemoryRankingOptions.Default;
        _memory = memory?.Value ?? new MemoryOptions();
    }

    /// <summary>Candidate ceiling for the centroid lookup, matching the ordinary over-fetch shape.</summary>
    private const int CentroidTopK = 20;

    /// <summary>Hops beyond which the boost is indistinguishable from noise at any sane γ.</summary>
    /// <remarks>
    /// At γ = 0.5 the fourth hop contributes 0.0625; the query caps at the same depth, so raising this
    /// alone would silently do nothing. Kept next to the Cypher's <c>*..4</c> for that reason.
    /// </remarks>
    private const int MaxHops = 4;

    /// <inheritdoc/>
    /// <remarks>
    /// Requires the structural decay to be switched on as well as the reranker: with γ = 1 every boost
    /// is 1.0, so running the two queries could not change an ordering and would be pure cost.
    /// </remarks>
    public bool IsEnabled => _memory.NodeDistanceReranking && _ranking.StructuralDecayEnabled;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemoryContextRankedItem>> RerankAsync(
        IReadOnlyList<MemoryContextRankedItem> candidates,
        MemoryRerankContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);

        // Facts are the only kind this understands: the distance query walks ABOUT/RELATED_TO from an
        // entity to a Fact. Silently reordering another kind by a measure that does not apply to it
        // would be worse than not running.
        if (context.SectionKind != MemoryItemKind.Fact) return candidates;
        if (candidates.Count < 2 || context.QueryEmbedding is not { Length: > 0 }) return candidates;

        var centroidId = await FindCentroidAsync(context, cancellationToken).ConfigureAwait(false);
        if (centroidId is null) return candidates;

        var hops = await MeasureHopsAsync(centroidId, candidates, cancellationToken).ConfigureAwait(false);
        if (hops.Count == 0) return candidates;

        var gamma = _ranking.EffectiveStructuralDecayGamma;

        // Multiplicative on the provider score, so a candidate the retrieval already ranked badly is
        // not promoted past a strong one by proximity alone -- graph closeness is evidence about
        // relevance, not a replacement for it. Unreachable candidates keep their score exactly.
        var reordered = candidates
            .Select(item => new
            {
                Item = item,
                Boosted = hops.TryGetValue(item.ItemId, out var h)
                    ? item.Score * (1.0 + Math.Pow(gamma, h))
                    : item.Score,
            })
            .OrderByDescending(scored => scored.Boosted)
            // Provider rank breaks ties, so an unreachable candidate and a 4-hop one with equal
            // boosted scores stay in the order retrieval put them, rather than an arbitrary one.
            .ThenBy(scored => scored.Item.RetrievalRank)
            .Select((scored, index) => scored.Item with { ContextRank = index + 1 })
            .ToList();

        _logger.LogDebug(
            "Node-distance rerank: {Reached} of {Total} candidates within {MaxHops} hops of centroid "
            + "{Centroid} (gamma {Gamma}).",
            hops.Count, candidates.Count, MaxHops, centroidId, gamma);

        return reordered;
    }

    private async Task<string?> FindCentroidAsync(
        MemoryRerankContext context, CancellationToken cancellationToken)
    {
        var hasOwner = context.Scope?.HasOwnerFilter == true;
        var includeShared = context.Scope?.IncludeShared ?? true;
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = context.QueryEmbedding!.ToList(),
            ["topK"] = CentroidTopK,
            ["minScore"] = 0.0,
        };
        if (hasOwner) parameters["ownerId"] = context.Scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                NodeDistanceQueries.CentroidEntity(hasOwner, includeShared), parameters)
                .ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count == 0 ? null : records[0]["entityId"].As<string>();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, int>> MeasureHopsAsync(
        string centroidId,
        IReadOnlyList<MemoryContextRankedItem> candidates,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["centroidId"] = centroidId,
            ["candidateIds"] = candidates.Select(item => item.ItemId).ToList(),
            ["maxHops"] = MaxHops,
        };

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                NodeDistanceQueries.DistanceToCandidates, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyDictionary<string, int>)records.ToDictionary(
                r => r["candidateId"].As<string>(),
                r => r["hops"].As<int>(),
                StringComparer.Ordinal);
        }, cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }
}
