using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryDecayService"/> class.
    /// </summary>
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
    /// <remarks>
    /// Portable placeholder: pruning requires server-side Cypher, so the actual implementation is the
    /// Neo4j adapter (<c>Neo4jMemoryDecayService</c>), which the Neo4j DI registration substitutes for
    /// this. This Core fallback (for a graph-less setup) is a no-op.
    /// </remarks>
    public Task<int> PruneExpiredMemoriesAsync(
        AgentMemory.Abstractions.Options.MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("PruneExpiredMemories requested (Core no-op placeholder), owner={Owner}", scope?.OwnerId);
        return Task.FromResult(0);
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
}
