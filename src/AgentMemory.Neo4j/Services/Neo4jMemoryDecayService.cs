using AgentMemory.Abstractions.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Neo4j-backed <see cref="IMemoryDecayService"/>. Runs the retention/decay formula server-side as Cypher:
/// <c>confidence × e^(−λ × daysSinceLastAccess) + boostFactor × accessCount</c> where
/// <c>λ = ln(2) / halfLifeDays</c>. Pruning targets Entity/Fact/Preference nodes whose score falls below the
/// configured minimum: by default (<see cref="MemoryDecayOptions.NonDestructive"/>) it <b>soft-invalidates</b>
/// them (stamps <c>invalidated_at</c> — kept, recoverable, auditable, dropped from live recall), or hard-deletes
/// when non-destructive is disabled. When a <see cref="MemoryScope"/> with an owner is supplied (R1) the prune is
/// owner-scoped (own nodes only — never another owner's, never shared/global); a null scope prunes globally (admin).
/// </summary>
internal sealed class Neo4jMemoryDecayService : IMemoryDecayService
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly IClock _clock;
    private readonly MemoryDecayOptions _options;
    private readonly ILogger<Neo4jMemoryDecayService> _logger;

    public Neo4jMemoryDecayService(
        INeo4jTransactionRunner tx,
        IClock clock,
        IOptions<MemoryDecayOptions> options,
        ILogger<Neo4jMemoryDecayService> logger)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Guard against a misconfigured half-life: lambda = ln(2)/halfLife would be Infinity/NaN for 0
        // and negative for <0, producing an inconsistent/destructive prune. (MemoryDecayOptions is
        // derived via Options.Create and bypasses the AddOptions().Validate() pipeline, so guard here.)
        if (_options.DecayHalfLifeDays <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.DecayHalfLifeDays,
                "MemoryDecayOptions.DecayHalfLifeDays must be greater than 0.");
    }

    /// <inheritdoc />
    public async Task<int> PruneExpiredMemoriesAsync(
        MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool nonDestructive = _options.NonDestructive;
        double lambda = Math.Log(2) / _options.DecayHalfLifeDays;
        string now = _clock.UtcNow.ToString("O");

        _logger.LogDebug(
            "Pruning expired memories (minScore={MinScore}, halfLifeDays={HalfLife}, owner={Owner}, nonDestructive={NonDestructive})",
            _options.MinRetentionScore, _options.DecayHalfLifeDays, scope?.OwnerId, nonDestructive);

        var queries = new[]
        {
            DecayQueries.PruneEntities(hasOwner, nonDestructive),
            DecayQueries.PruneFacts(hasOwner, nonDestructive),
            DecayQueries.PrunePreferences(hasOwner, nonDestructive),
        };

        return await _tx.WriteAsync(async runner =>
        {
            int total = 0;
            foreach (var cypher in queries)
            {
                var parameters = new Dictionary<string, object?>
                {
                    ["now"] = now,
                    ["lambda"] = lambda,
                    ["boostFactor"] = _options.AccessBoostFactor,
                    ["minScore"] = _options.MinRetentionScore,
                };
                if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

                var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
                var records = await cursor.ToListAsync().ConfigureAwait(false);
                if (records.Count > 0)
                    total += Convert.ToInt32(records[0]["pruned"]);
            }

            _logger.LogInformation(
                "{Mode} {Count} expired memory node(s), owner={Owner}",
                nonDestructive ? "Soft-invalidated" : "Pruned", total, scope?.OwnerId);
            return total;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<double> CalculateRetentionScoreAsync(
        string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        // The label is interpolated into Cypher, so guard against an out-of-range cast (which would ToString()
        // to a numeric label and cause a Neo4j syntax error) — fail fast with a clear argument error instead.
        if (!Enum.IsDefined(nodeKind))
            throw new ArgumentOutOfRangeException(nameof(nodeKind), nodeKind, "Unknown MemoryNodeKind.");
        var label = nodeKind.ToString(); // enum name == Neo4j label (Entity/Fact/Preference)

        var cypher = DecayQueries.GetRetentionFields(label);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = nodeId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return 0.0;

            var r = records[0];
            // Parity with the prune (which requires created_at IS NOT NULL): a node with no created_at
            // is never prune-eligible, so report it as score 0 rather than letting the non-nullable
            // datetime helper default it to "now" (which would score it as artificially fresh).
            if (r["createdAt"] is null) return 0.0;
            double confidence = r["confidence"] is null ? 0.5 : Convert.ToDouble(r["confidence"]);
            var createdAt = Neo4jDateTimeHelper.ReadDateTimeOffset(r["createdAt"]);
            var lastAccessedAt = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(r["lastAccessedAt"]);
            int accessCount = r["accessCount"] is null ? 0 : Convert.ToInt32(r["accessCount"]);

            return ComputeScore(confidence, createdAt, lastAccessedAt, accessCount);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAccessTimestampAsync(
        string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        // The label is interpolated into Cypher, so guard against an out-of-range cast (which would ToString()
        // to a numeric label and cause a Neo4j syntax error) — fail fast with a clear argument error instead.
        if (!Enum.IsDefined(nodeKind))
            throw new ArgumentOutOfRangeException(nameof(nodeKind), nodeKind, "Unknown MemoryNodeKind.");
        var label = nodeKind.ToString(); // enum name == Neo4j label (Entity/Fact/Preference)

        var cypher = DecayQueries.UpdateAccessTimestamp(label);
        string now = _clock.UtcNow.ToString("O");

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { id = nodeId, kind = label, now }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Bumped access timestamp for {Label} {NodeId}", label, nodeId);
    }

    /// <summary>
    /// Computes the retention score from raw field values, mirroring the server-side prune formula.
    /// </summary>
    internal double ComputeScore(
        double confidence, DateTimeOffset createdAt, DateTimeOffset? lastAccessedAt, int accessCount)
    {
        var reference = lastAccessedAt ?? createdAt;
        double daysSince = Math.Max(0, (_clock.UtcNow - reference).TotalDays);
        double lambda = Math.Log(2) / _options.DecayHalfLifeDays;
        return confidence * Math.Exp(-lambda * daysSince) + _options.AccessBoostFactor * accessCount;
    }
}
