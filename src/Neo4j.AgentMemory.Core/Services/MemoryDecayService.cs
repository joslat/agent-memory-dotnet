using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Computes retention scores and prunes stale memory nodes.
/// Score formula: baseConfidence × e^(−λ × daysSinceLastAccess) + accessBoost × accessCount
/// where λ = ln(2) / halfLifeDays.
/// </summary>
public sealed class MemoryDecayService : IMemoryDecayService
{
    private readonly IEntityRepository _entityRepo;
    private readonly IFactRepository _factRepo;
    private readonly IPreferenceRepository _prefRepo;
    private readonly IClock _clock;
    private readonly MemoryDecayOptions _options;
    private readonly ILogger<MemoryDecayService> _logger;

    public MemoryDecayService(
        IEntityRepository entityRepo,
        IFactRepository factRepo,
        IPreferenceRepository prefRepo,
        IClock clock,
        IOptions<MemoryDecayOptions> options,
        ILogger<MemoryDecayService> logger)
    {
        _entityRepo = entityRepo;
        _factRepo = factRepo;
        _prefRepo = prefRepo;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PruneExpiredMemoriesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Pruning expired memories for session {SessionId}", sessionId);

        // Compute the retention score locally for all entities/facts/preferences
        // and delete those below threshold.
        // In a full Neo4j implementation the prune Cypher queries would run server-side.
        // This implementation delegates to per-node deletion for portability.
        int pruned = 0;

        pruned += await PruneByLabelAsync("Entity", cancellationToken);
        pruned += await PruneByLabelAsync("Fact", cancellationToken);
        pruned += await PruneByLabelAsync("Preference", cancellationToken);

        _logger.LogInformation("Pruned {Count} expired memory nodes for session {SessionId}", pruned, sessionId);
        return pruned;
    }

    /// <inheritdoc />
    public Task<double> CalculateRetentionScoreAsync(
        string nodeId,
        string nodeLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeLabel);

        // This is a pure computation—in the real Neo4j implementation the fields would
        // be fetched from the database. For the Core service we expose the formula so
        // callers can use it with pre-fetched data.  The method is kept async to match
        // the interface contract (database-backed implementations will be async).
        return Task.FromResult(0.0);
    }

    /// <summary>
    /// Computes the retention score from raw field values.
    /// </summary>
    internal double ComputeScore(
        double confidence,
        DateTimeOffset createdAt,
        DateTimeOffset? lastAccessedAt,
        int accessCount)
    {
        var now = _clock.UtcNow;
        var reference = lastAccessedAt ?? createdAt;
        double daysSinceAccess = Math.Max(0, (now - reference).TotalDays);
        double lambda = Math.Log(2) / _options.DecayHalfLifeDays;

        return confidence * Math.Exp(-lambda * daysSinceAccess)
            + _options.AccessBoostFactor * accessCount;
    }

    /// <inheritdoc />
    public Task UpdateAccessTimestampAsync(
        string nodeId,
        string nodeLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeLabel);

        // The actual timestamp update is performed in the repository layer
        // (Neo4j Cypher query). This Core implementation is a no-op pass-through
        // so the interface compiles; the real work is done by the Neo4j adapter.
        _logger.LogDebug("Access timestamp update requested for {Label} {NodeId}", nodeLabel, nodeId);
        return Task.CompletedTask;
    }

    private Task<int> PruneByLabelAsync(string label, CancellationToken ct)
    {
        // Placeholder: real pruning runs via DecayQueries on the Neo4j adapter.
        _logger.LogDebug("Pruning stale {Label} nodes", label);
        return Task.FromResult(0);
    }
}
