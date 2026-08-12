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
/// Boosts facts the conversation keeps returning to (R7).
/// </summary>
/// <remarks>
/// <para>
/// A fact the world asserted five times is usually more central than one mentioned once. Similarity
/// cannot see that: two facts phrased alike score alike however often either was actually said.
/// </para>
/// <para>
/// <b>The signal is ingestion-side, and that distinction is the whole task.</b> It counts how often
/// <i>the world</i> re-asserted a fact — <c>mention_count</c>, incremented by the upsert paths when a
/// new ingestion re-states an existing triple. The tempting substitute, <c>:MemoryReadAudit</c>,
/// counts how often <i>we</i> surfaced it, and ranking on that is a rich-get-richer loop: whatever
/// ranks highly gets retrieved, retrieval raises the count, the count raises the rank. It would look
/// like learning and be self-reinforcement, so it is not used here and must not be.
/// </para>
/// <para>
/// <b>Logarithmic, not linear.</b> The gap between one mention and three is real; the gap between
/// thirty and thirty-two is noise, and a linear boost would let a chatty topic bury a precise answer.
/// </para>
/// <para>
/// One bounded query for the whole candidate set. Off by default: it changes recall ordering and
/// every recorded measurement was taken without it.
/// </para>
/// </remarks>
internal sealed class MentionFrequencyReranker : IMemoryReranker
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly MemoryOptions _memory;
    private readonly ILogger<MentionFrequencyReranker> _logger;

    public MentionFrequencyReranker(
        INeo4jTransactionRunner tx,
        ILogger<MentionFrequencyReranker> logger,
        IOptions<MemoryOptions>? memory = null)
    {
        _tx = tx;
        _logger = logger;
        _memory = memory?.Value ?? new MemoryOptions();
    }

    /// <summary>
    /// How much a well-attested fact may outrank a better-matching one.
    /// </summary>
    /// <remarks>
    /// At 0.15, a fact mentioned eight times gains ~1.31× while a once-mentioned one gains 1.0 — enough
    /// to break a near-tie, not enough to promote a weak match over a strong one. Salience is a
    /// tiebreaker among plausible answers, not a substitute for matching the question.
    /// </remarks>
    private const double BoostWeight = 0.15;

    /// <inheritdoc/>
    public bool IsEnabled => _memory.MentionFrequencyReranking;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemoryContextRankedItem>> RerankAsync(
        IReadOnlyList<MemoryContextRankedItem> candidates,
        MemoryRerankContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);

        // mention_count is maintained on Fact only. Boosting another kind by a counter nothing
        // increments would be a no-op dressed as a feature.
        if (context.SectionKind != MemoryItemKind.Fact || candidates.Count < 2) return candidates;

        var mentions = await MentionCountsAsync(candidates, cancellationToken).ConfigureAwait(false);
        if (mentions.Count == 0) return candidates;

        var reordered = candidates
            .Select(item => new
            {
                Item = item,
                Boosted = item.Score * (1.0 + (BoostWeight * Math.Log(
                    mentions.TryGetValue(item.ItemId, out var count) ? Math.Max(count, 1) : 1))),
            })
            .OrderByDescending(scored => scored.Boosted)
            // Retrieval rank breaks ties, so equal salience leaves the provider's order intact rather
            // than an arbitrary one.
            .ThenBy(scored => scored.Item.RetrievalRank)
            .Select((scored, index) => scored.Item with { ContextRank = index + 1 })
            .ToList();

        _logger.LogDebug(
            "Mention-frequency rerank over {Count} candidates; most-mentioned {Max}.",
            candidates.Count, mentions.Count == 0 ? 0 : mentions.Values.Max());

        return reordered;
    }

    private async Task<IReadOnlyDictionary<string, int>> MentionCountsAsync(
        IReadOnlyList<MemoryContextRankedItem> candidates, CancellationToken cancellationToken) =>
        await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                FactQueries.MentionCounts,
                new Dictionary<string, object?>
                {
                    ["candidateIds"] = candidates.Select(item => item.ItemId).ToList(),
                }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyDictionary<string, int>)records.ToDictionary(
                r => r["candidateId"].As<string>(),
                r => r["mentions"].As<int>(),
                StringComparer.Ordinal);
        }, cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, int>(StringComparer.Ordinal);
}
