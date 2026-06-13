using Microsoft.Extensions.Logging;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace AgentMemory.Analytics;

/// <inheritdoc/>
public sealed class GdsAvailability : IGdsAvailability
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<GdsAvailability> _logger;
    private readonly object _gate = new();
    private Task<bool>? _probe;

    public GdsAvailability(INeo4jTransactionRunner tx, ILogger<GdsAvailability> logger)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        // Memoize the probe — GDS availability doesn't change during the process lifetime.
        lock (_gate) { return _probe ??= ProbeAsync(); }
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
            var version = await _tx.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(GdsQueries.ProbeVersion);
                var record = await cursor.SingleAsync();
                return record["version"].As<string>();
            });
            _logger.LogInformation("Neo4j GDS plugin detected (version {Version}); analytics enabled.", version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Neo4j GDS plugin not available ({Reason}); analytics will return empty results. " +
                "Install the Graph Data Science plugin to enable PageRank / community detection.",
                ex.GetType().Name);
            return false;
        }
    }
}
