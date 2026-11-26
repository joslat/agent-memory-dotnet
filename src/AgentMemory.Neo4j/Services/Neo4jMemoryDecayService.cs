using AgentMemory.Abstractions.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Core.Services;
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
internal sealed class Neo4jMemoryDecayService : IMemoryDecayService, IMemoryReadRecorder
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly TimeProvider _clock;
    private readonly MemoryDecayOptions _options;
    private readonly ILogger<Neo4jMemoryDecayService> _logger;
    private readonly bool _workingMemoryEnabled;
    private readonly IMemoryAccessTracker? _accessTracker;

    public Neo4jMemoryDecayService(
        INeo4jTransactionRunner tx,
        TimeProvider clock,
        IOptions<MemoryDecayOptions> options,
        ILogger<Neo4jMemoryDecayService> logger,
        IOptions<MemoryOptions>? memoryOptions = null,
        IMemoryAccessTracker? accessTracker = null)
    {
        _workingMemoryEnabled = memoryOptions?.Value.WorkingMemory.Enabled ?? false;
        _accessTracker = accessTracker;
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
        // DRT-5: a memory recalled a moment ago is not pruned on a count its queued read note has not raised yet.
        if (_accessTracker is not null) await _accessTracker.FlushAsync(cancellationToken).ConfigureAwait(false);
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool nonDestructive = _options.NonDestructive;
        double lambda = Math.Log(2) / _options.DecayHalfLifeDays;
        string now = _clock.GetUtcNow().ToString("O");

        _logger.LogDebug(
            "Pruning expired memories (minScore={MinScore}, halfLifeDays={HalfLife}, owner={Owner}, nonDestructive={NonDestructive})",
            _options.MinRetentionScore, _options.DecayHalfLifeDays, scope?.OwnerId, nonDestructive);

        var queries = new[]
        {
            DecayQueries.PruneEntities(hasOwner, nonDestructive),
            DecayQueries.PruneFacts(hasOwner, nonDestructive),
            DecayQueries.PrunePreferences(hasOwner, nonDestructive),
        };

        // RVW-REST-STORAGE (the review's storage-7): a batch per transaction, repeated until one comes back short, so the
        // prune never holds a whole store's changes in one transaction (out of transaction memory, a timeout, and the
        // driver's retry replaying it all). Each batch progresses: a soft prune skips what it already invalidated, a hard
        // one deletes.
        int total = 0;
        foreach (var cypher in queries)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["now"] = now,
                ["lambda"] = lambda,
                ["boostFactor"] = _options.AccessBoostFactor,
                ["maxBoost"] = _options.MaxAccessBoost,
                ["minScore"] = _options.MinRetentionScore,
                ["batch"] = PruneBatchSize,
            };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pruned = await _tx.WriteAsync(async runner =>
                {
                    var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
                    var records = await cursor.ToListAsync().ConfigureAwait(false);
                    var count = records.Count > 0 ? Convert.ToInt32(records[0]["pruned"]) : 0;
                    // The working-memory block is compiled from these facts, preferences and entities. After a prune
                    // (a hard one is storage reclamation, including for erasure) it must not keep serving the removed
                    // text: cleared in the same transaction as each batch that removed something, rebuilt on the
                    // owner's next read.
                    if (count > 0 && _workingMemoryEnabled)
                    {
                        var clear = new Dictionary<string, object?> { ["now"] = now };
                        if (hasOwner) clear["ownerId"] = scope!.OwnerId;
                        await runner.RunAsync(hasOwner ? WorkingMemoryQueries.ClearBlock : WorkingMemoryQueries.ClearAllBlocks, clear)
                            .ConfigureAwait(false);
                    }
                    return count;
                }, cancellationToken).ConfigureAwait(false);
                total += pruned;
                if (pruned < PruneBatchSize) break;
            }
        }

        _logger.LogInformation(
            "{Mode} {Count} expired memory node(s), owner={Owner}",
            nonDestructive ? "Soft-invalidated" : "Pruned", total, scope?.OwnerId);
        return total;
    }

    /// <summary>How many nodes one prune transaction changes at most (storage-7).</summary>
    internal int PruneBatchSize { get; init; } = 2000;

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
        string now = _clock.GetUtcNow().ToString("O");

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { id = nodeId, kind = label, now }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Bumped access timestamp for {Label} {NodeId}", label, nodeId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One write transaction containing one <c>UNWIND</c> query per distinct node kind, replacing the
    /// interface default's one-transaction-per-node loop. This is the pre-model critical path: measured
    /// at shipped recall defaults, the loop cost 25 write transactions (and 25 sessions) per turn before
    /// the model was invoked. Per-node effects are unchanged — same timestamp, same access-count
    /// increment, same <c>:MemoryReadAudit</c> row.
    /// </remarks>
    public async Task UpdateAccessTimestampsAsync(
        IReadOnlyCollection<(string NodeId, MemoryNodeKind NodeKind)> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0) return;

        // Grouped by kind, each id once per kind (ByKind): the per-node form could not count an id twice, so guarding
        // here is not paranoia — it is preserving the previous semantics.
        var byKind = ByKind(nodes);

        string now = _clock.GetUtcNow().ToString("O");

        // A single transaction for every kind: the whole point is to stop paying per-node transaction
        // and session overhead on the read path. It is also more atomic than the loop it replaces, which
        // could partially fail across 25 independent transactions.
        await _tx.WriteAsync(runner => WriteRecordAsync(runner, byKind, now), cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Bumped access timestamps for {NodeCount} nodes across {KindCount} kinds in one transaction.",
            byKind.Sum(kv => kv.Value.Count), byKind.Count);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// DRT-5 (way A of the read-records design): one write transaction, and in it, read after read in the order given,
    /// the same statement <see cref="UpdateAccessTimestampsAsync"/> runs, with that read's own time. So the stored state
    /// is exactly that of one write per read, by construction, and the statement text does not change. The driver sends
    /// the statements without waiting for each, so the transaction costs about the round trips of one.
    /// </remarks>
    async Task<int> IMemoryReadRecorder.RecordReadsAsync(IReadOnlyList<MemoryRead> reads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reads);
        var records = reads.Select(read => (ByKind: ByKind(read.Nodes), Now: Stamp(read.ReadAtUtc)))
            .Where(record => record.ByKind.Count > 0).ToList();
        if (records.Count == 0) return reads.Count;

        try
        {
            await _tx.WriteAsync(async runner =>
            {
                foreach (var (byKind, now) in records) await WriteRecordAsync(runner, byKind, now).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Recorded {Reads} reads in one transaction.", records.Count);
            return reads.Count;
        }
        catch (Exception ex) when (records.Count > 1 && !Unreachable(ex) && ex is not OperationCanceledException)
        {
            // A transaction is all or nothing, so writing the reads again cannot count one twice. Each on its own, once:
            // never worse than one write per read, and one bad read does not take the others with it.
            _logger.LogWarning(ex, "Recording {Reads} reads together failed; writing them one by one.", records.Count);
            var failed = 0;
            foreach (var (byKind, now) in records)
            {
                try
                {
                    await _tx.WriteAsync(runner => WriteRecordAsync(runner, byKind, now), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception single) when (!Unreachable(single) && single is not OperationCanceledException)
                {
                    failed++;
                    _logger.LogWarning(single, "A read record failed on its own as well; its {Count} item(s) are not recorded.",
                        byKind.Sum(kv => kv.Value.Count));
                }
            }
            return reads.Count - failed;
        }
    }

    /// <summary>The driver has given up reaching the store; nothing written one by one would do better.</summary>
    private static bool Unreachable(Exception ex) => ex is ServiceUnavailableException or SessionExpiredException;

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Groups one read's items by kind (the label is interpolated into Cypher, so it cannot be a parameter) and makes the
    /// ids distinct within each kind: UNWIND yields one row per element, so a repeated id would increment access_count
    /// twice and write two audit rows.
    /// </summary>
    private static Dictionary<MemoryNodeKind, HashSet<string>> ByKind(IEnumerable<(string NodeId, MemoryNodeKind NodeKind)> nodes)
    {
        var byKind = new Dictionary<MemoryNodeKind, HashSet<string>>();
        foreach (var (nodeId, nodeKind) in nodes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
            if (!Enum.IsDefined(nodeKind))
                throw new ArgumentOutOfRangeException(nameof(nodes), nodeKind, "Unknown MemoryNodeKind.");

            if (!byKind.TryGetValue(nodeKind, out var ids))
                byKind[nodeKind] = ids = new HashSet<string>(StringComparer.Ordinal);
            ids.Add(nodeId);
        }
        return byKind;
    }

    /// <summary>One read's statements, one per kind, inside the caller's transaction.</summary>
    private static async Task WriteRecordAsync(IAsyncQueryRunner runner, Dictionary<MemoryNodeKind, HashSet<string>> byKind, string now)
    {
        foreach (var (nodeKind, ids) in byKind)
        {
            var label = nodeKind.ToString(); // enum name == Neo4j label (Entity/Fact/Preference)
            await runner.RunAsync(
                DecayQueries.UpdateAccessTimestampBatch(label),
                new { ids = ids.ToList(), kind = label, now }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes the retention score from raw field values, mirroring the server-side prune formula.
    /// </summary>
    internal double ComputeScore(
        double confidence, DateTimeOffset createdAt, DateTimeOffset? lastAccessedAt, int accessCount)
    {
        var reference = lastAccessedAt ?? createdAt;
        double daysSince = Math.Max(0, (_clock.GetUtcNow() - reference).TotalDays);
        double lambda = Math.Log(2) / _options.DecayHalfLifeDays;
        // BUG-R7: damped, capped, and decayed on the same curve as confidence. Must stay identical to
        // MemoryDecayService.ComputeScore and to the Cypher prune/rerank expressions.
        double boost = Math.Min(
            _options.AccessBoostFactor * Math.Log(1 + Math.Max(0, accessCount)),
            _options.MaxAccessBoost);
        return (confidence + boost) * Math.Exp(-lambda * daysSince);
    }
}
