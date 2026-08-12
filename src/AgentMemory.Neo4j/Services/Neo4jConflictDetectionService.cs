using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

using AgentMemory.Abstractions.Options;
namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Neo4j-backed <see cref="IConflictDetectionService"/>. Detect-only: runs read Cypher grouping facts
/// by subject + predicate within an owner scope and reports groups with multiple distinct objects.
/// </summary>
internal sealed class Neo4jConflictDetectionService : IConflictDetectionService
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly IClock _clock;
    private readonly ILogger<Neo4jConflictDetectionService> _logger;

    public Neo4jConflictDetectionService(
        INeo4jTransactionRunner tx,
        IClock clock,
        ILogger<Neo4jConflictDetectionService> logger,
        IOptions<MemoryOptions>? memoryOptions = null)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // S2. This is the SECOND caller of the supersede query -- the offline hygiene pass, next to
        // the write-time one -- and a contradiction resolved here is the same event either way. An
        // integration test caught the omission; the unit tests could not, because the missing
        // parameter is only rejected by the server.
        _reinforceAlpha = memoryOptions?.Value.ConfidenceReinforcementAlpha ?? 0.0;
    }

    private readonly double _reinforceAlpha;

    /// <inheritdoc/>
    public async Task<ConflictReport> DetectConflictsAsync(
        ConflictDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new ConflictDetectionOptions();
        var ranAt = _clock.UtcNow;

        var factConflicts = opts.DetectFactContradictions
            ? await DetectFactContradictionsAsync(opts, cancellationToken).ConfigureAwait(false)
            : Array.Empty<FactConflict>();

        _logger.LogInformation("Conflict detection complete: {FactConflicts} fact contradiction group(s).", factConflicts.Count);

        return new ConflictReport { RanAtUtc = ranAt, FactConflicts = factConflicts };
    }

    /// <inheritdoc/>
    public async Task<ConflictResolutionResult> ResolveFactContradictionsAsync(
        ConflictResolutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new ConflictResolutionOptions();
        var ranAt = _clock.UtcNow;

        // Re-use the detection query, then act on each group. Resolution is the opt-in mutating path;
        // DetectConflictsAsync stays the non-mutating default.
        // Do NOT gate group membership by confidence here: gating members could hide the genuine
        // highest-confidence assertion and promote a weaker fact to "winner". Detection sees the full
        // (live) group; MinConfidence is applied below as a floor on the chosen winner.
        var conflicts = await DetectFactContradictionsAsync(
            new ConflictDetectionOptions
            {
                DetectFactContradictions = true,
                MinConfidence = null,
                MaxConflicts = opts.MaxConflicts,
            },
            cancellationToken).ConfigureAwait(false);

        int groupsResolved = 0;
        int factsSuperseded = 0;
        string now = ranAt.UtcDateTime.ToString("O");

        foreach (var conflict in conflicts)
        {
            // Winner = highest-confidence assertion; ties broken deterministically by fact id.
            var ordered = conflict.Values
                .OrderByDescending(v => v.Confidence)
                .ThenBy(v => v.FactId, StringComparer.Ordinal)
                .ToList();
            var winner = ordered[0];
            var losers = ordered.Skip(1).ToList();
            if (losers.Count == 0) continue;

            // Winner floor: don't auto-resolve a contradiction whose best assertion is itself weak.
            if (opts.MinConfidence is double floor && winner.Confidence < floor) continue;

            bool hasOwner = conflict.OwnerId is not null;
            var cypher = FactQueries.Supersede(hasOwner);

            int closedInGroup = await _tx.WriteAsync(async runner =>
            {
                int closed = 0;
                foreach (var loser in losers)
                {
                    var parameters = new Dictionary<string, object?>
                    {
                        ["loserId"] = loser.FactId,
                        ["winnerId"] = winner.FactId,
                        ["now"] = now,
                        ["reinforceAlpha"] = _reinforceAlpha,
                    };
                    if (hasOwner) parameters["ownerId"] = conflict.OwnerId;
                    var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
                    var records = await cursor.ToListAsync().ConfigureAwait(false);
                    if (records.Count > 0 && records[0]["superseded"].As<bool>()) closed++;
                }
                return closed;
            }, cancellationToken).ConfigureAwait(false);

            if (closedInGroup > 0)
            {
                groupsResolved++;
                factsSuperseded += closedInGroup;
            }
        }

        _logger.LogInformation(
            "Conflict resolution complete: {Groups} group(s) resolved, {Facts} fact(s) superseded.",
            groupsResolved, factsSuperseded);

        return new ConflictResolutionResult
        {
            RanAtUtc = ranAt,
            ConflictsResolved = groupsResolved,
            FactsSuperseded = factsSuperseded,
        };
    }

    private Task<IReadOnlyList<FactConflict>> DetectFactContradictionsAsync(
        ConflictDetectionOptions opts, CancellationToken cancellationToken) =>
        _tx.ReadAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["minConfidence"] = opts.MinConfidence,
                ["limit"] = opts.MaxConflicts,
            };

            var cursor = await runner.RunAsync(ConflictQueries.DetectFactContradictions, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);

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
        }, cancellationToken);
}
