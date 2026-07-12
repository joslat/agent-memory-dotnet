using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace AgentMemory.Analytics;

/// <inheritdoc/>
internal sealed class MemoryPageRankService : IMemoryPageRankService
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly IGdsAvailability _gds;
    private readonly GdsAnalyticsOptions _options;
    private readonly ILogger<MemoryPageRankService> _logger;

    public MemoryPageRankService(
        INeo4jTransactionRunner tx,
        IGdsAvailability gds,
        IOptions<GdsAnalyticsOptions> options,
        ILogger<MemoryPageRankService> logger)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _gds = gds ?? throw new ArgumentNullException(nameof(gds));
        _options = options?.Value ?? new GdsAnalyticsOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<EntityRank>> RankEntitiesAsync(
        int? topN = null, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        if (topN is not null)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN.Value, nameof(topN));

        if (!await _gds.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("PageRank skipped — Neo4j GDS plugin not installed; returning no ranks.");
            return Array.Empty<EntityRank>();
        }

        var limit = topN ?? _options.DefaultTopN;
        var graphName = $"am_pagerank_{Guid.NewGuid():N}";

        return await GdsGraphScope.WithProjectionAsync(_tx, graphName, scope, async runner =>
        {
            var cursor = await runner.RunAsync(GdsQueries.PageRankStream, new { graphName, topN = limit }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyList<EntityRank>)records
                .Select(r => new EntityRank(r["entityId"].As<string>(), r["score"].As<double>()))
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
}
