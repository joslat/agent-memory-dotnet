using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace AgentMemory.Analytics;

/// <inheritdoc/>
internal sealed class MemoryCommunityService : IMemoryCommunityService
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly IGdsAvailability _gds;
    private readonly ILogger<MemoryCommunityService> _logger;

    public MemoryCommunityService(
        INeo4jTransactionRunner tx,
        IGdsAvailability gds,
        ILogger<MemoryCommunityService> logger)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _gds = gds ?? throw new ArgumentNullException(nameof(gds));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<EntityCommunity>> DetectCommunitiesAsync(
        MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        if (!await _gds.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Community detection skipped — Neo4j GDS plugin not installed; returning no communities.");
            return Array.Empty<EntityCommunity>();
        }

        var graphName = $"am_louvain_{Guid.NewGuid():N}";

        return await GdsGraphScope.WithProjectionAsync(_tx, graphName, scope, async runner =>
        {
            var cursor = await runner.RunAsync(GdsQueries.LouvainStream, new { graphName }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyList<EntityCommunity>)records
                .Select(r => new EntityCommunity(r["entityId"].As<string>(), r["communityId"].As<long>()))
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }
}
