using Microsoft.Extensions.Logging;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace AgentMemory.Analytics;

/// <inheritdoc/>
internal sealed class GdsAvailability : IGdsAvailability
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<GdsAvailability> _logger;
    private readonly object _gate = new();
    private bool? _available;       // definitive cached result (present / genuinely not installed); null = undetermined

    public GdsAvailability(INeo4jTransactionRunner tx, ILogger<GdsAvailability> logger)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        // Memoize only a DEFINITIVE answer — GDS present, or genuinely not installed. A transient probe
        // failure (a connectivity/timeout blip) is NOT cached, so a first-use that happens to land during a
        // Neo4j outage cannot disable analytics for the whole process lifetime: the next call re-probes.
        lock (_gate)
        {
            if (_available is { } cached) return cached;
        }

        var (available, definitive) = await ProbeAsync().ConfigureAwait(false);
        if (definitive)
            lock (_gate) { _available = available; }
        return available;
    }

    /// <summary>Probes for GDS. Returns whether it is available and whether that answer is definitive (stable).</summary>
    private async Task<(bool Available, bool Definitive)> ProbeAsync()
    {
        try
        {
            var version = await _tx.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(GdsQueries.ProbeVersion).ConfigureAwait(false);
                var record = await cursor.SingleAsync().ConfigureAwait(false);
                return record["version"].As<string>();
            }).ConfigureAwait(false);

            _logger.LogInformation("Neo4j GDS plugin detected (version {Version}); analytics enabled.", version);
            return (true, true);
        }
        catch (ClientException ex)
        {
            // A client error on `RETURN gds.version()` means the function is unknown — GDS is genuinely not
            // installed. That is a stable answer, so it is memoized by the caller.
            _logger.LogWarning(ex,
                "Neo4j GDS plugin not available; analytics will return empty results. " +
                "Install the Graph Data Science plugin to enable PageRank / community detection.");
            return (false, true);
        }
        catch (Exception ex)
        {
            // Transient failure (connectivity/timeout/transaction). Degrade to unavailable for THIS call but
            // do NOT cache it — the next IsAvailableAsync re-probes once Neo4j recovers.
            _logger.LogWarning(ex,
                "Neo4j GDS availability probe failed transiently; treating as unavailable for now and will " +
                "re-probe on next use.");
            return (false, false);
        }
    }
}
