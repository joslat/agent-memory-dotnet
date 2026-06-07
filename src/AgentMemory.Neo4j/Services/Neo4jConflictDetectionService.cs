using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Neo4j-backed <see cref="IConflictDetectionService"/>. Detect-only: runs read Cypher grouping facts
/// by subject + predicate within an owner scope and reports groups with multiple distinct objects.
/// </summary>
public sealed class Neo4jConflictDetectionService : IConflictDetectionService
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly IClock _clock;
    private readonly ILogger<Neo4jConflictDetectionService> _logger;

    public Neo4jConflictDetectionService(
        INeo4jTransactionRunner tx,
        IClock clock,
        ILogger<Neo4jConflictDetectionService> logger)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<ConflictReport> DetectConflictsAsync(
        ConflictDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new ConflictDetectionOptions();
        var ranAt = _clock.UtcNow;

        var factConflicts = opts.DetectFactContradictions
            ? await DetectFactContradictionsAsync(opts, cancellationToken)
            : Array.Empty<FactConflict>();

        _logger.LogInformation("Conflict detection complete: {FactConflicts} fact contradiction group(s).", factConflicts.Count);

        return new ConflictReport { RanAtUtc = ranAt, FactConflicts = factConflicts };
    }

    private Task<IReadOnlyList<FactConflict>> DetectFactContradictionsAsync(
        ConflictDetectionOptions opts, CancellationToken ct) =>
        _tx.ReadAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["minConfidence"] = opts.MinConfidence,
                ["limit"] = opts.MaxConflicts,
            };

            var cursor = await runner.RunAsync(ConflictQueries.DetectFactContradictions, parameters);
            var records = await cursor.ToListAsync();

            return (IReadOnlyList<FactConflict>)records.Select(r =>
            {
                var ownerKey = r["ownerKey"].As<string>();
                var members = r["members"].As<List<object>>()
                    .Cast<IReadOnlyDictionary<string, object>>()
                    .Select(m => new ConflictingFactValue(
                        m["factId"].As<string>(),
                        m["object"].As<string>(),
                        Convert.ToDouble(m["confidence"])))
                    .ToList();

                return new FactConflict(
                    r["subject"].As<string>(),
                    r["predicate"].As<string>(),
                    ownerKey == "*" ? null : ownerKey,
                    members);
            }).ToList();
        }, ct);
}
