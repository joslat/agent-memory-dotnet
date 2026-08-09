using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Core.Memory;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

internal sealed class SchemaBootstrapper : ISchemaBootstrapper
{
    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<SchemaBootstrapper> _logger;
    private readonly string[] _vectorIndexes;
    private readonly int _embeddingDimensions;
    private readonly bool _validateVectorIndexDimensions;

    /// <summary>Bounded so a large store migrates in pages rather than one transaction.</summary>
    internal const int CanonicalKeyBackfillBatchSize = 500;

    public SchemaBootstrapper(
        INeo4jTransactionRunner txRunner,
        IOptions<Neo4jOptions> options,
        ILogger<SchemaBootstrapper> logger)
    {
        _txRunner = txRunner;
        _logger = logger;

        _embeddingDimensions = options.Value.EmbeddingDimensions;
        _validateVectorIndexDimensions = options.Value.ValidateVectorIndexDimensions;
        _vectorIndexes = SchemaQueries.BuildVectorIndexes(_embeddingDimensions);
    }

    /// <summary>
    /// Facts written before canonical identity carry no <c>*_key</c> properties, so a re-extracted
    /// triple never MERGEs onto them and predicate expansion cannot see them. Backfills the keys
    /// during bootstrap, before any write can occur on an upgraded store.
    /// </summary>
    /// <remarks>
    /// Computed in C# and never in Cypher: <c>toLower()</c> and <see cref="string.ToLowerInvariant"/>
    /// disagree on U+0130, so a Cypher backfill would write keys the write path never matches.
    /// Idempotent by construction — it selects on <c>predicate_key IS NULL</c>, so a re-run over a
    /// migrated store does nothing.
    /// </remarks>
    internal async Task<int> BackfillCanonicalFactKeysAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var migrated = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = await _txRunner.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(
                    FactQueries.SelectFactsMissingCanonicalKeys,
                    new { limit = batchSize }).ConfigureAwait(false);
                var records = await cursor.ToListAsync().ConfigureAwait(false);
                return records.Select(record => new CanonicalKeyBackfillRow(
                    record["id"].As<string>(),
                    record["subject"].As<string>(),
                    record["predicate"].As<string>(),
                    record["object"].As<string>())).ToList();
            }, cancellationToken).ConfigureAwait(false);

            if (pending.Count == 0)
                break;

            var items = pending.Select(fact => new Dictionary<string, object?>
            {
                ["id"] = fact.Id,
                ["subject_key"] = MemoryTripleCanonicalizer.CanonicalValue(fact.Subject),
                ["predicate_key"] = MemoryTripleCanonicalizer.Canonical(fact.Predicate),
                ["object_key"] = MemoryTripleCanonicalizer.CanonicalValue(fact.Object)
            }).ToList();

            await _txRunner.WriteAsync(async runner =>
            {
                var cursor = await runner.RunAsync(
                    FactQueries.ApplyCanonicalKeys, new { items }).ConfigureAwait(false);
                await cursor.ConsumeAsync().ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            migrated += pending.Count;

            // A short final batch means the last page was reached; anything else would re-query for
            // a page that cannot exist.
            if (pending.Count < batchSize)
                break;
        }

        if (migrated > 0)
        {
            _logger.LogInformation(
                "Backfilled canonical identity keys onto {Count} pre-existing facts.", migrated);
        }

        return migrated;
    }

    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Running schema bootstrap: {ConstraintCount} constraints, {FulltextCount} fulltext indexes, " +
            "{VectorCount} vector indexes, {PropertyCount} property indexes.",
            SchemaQueries.Constraints.Length, SchemaQueries.FulltextIndexes.Length,
            _vectorIndexes.Length, SchemaQueries.PropertyIndexes.Length);

        foreach (var constraint in SchemaQueries.Constraints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(constraint, cancellationToken).ConfigureAwait(false);
        }

        foreach (var index in SchemaQueries.FulltextIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken).ConfigureAwait(false);
        }

        foreach (var index in _vectorIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken).ConfigureAwait(false);
        }

        foreach (var index in SchemaQueries.PropertyIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken).ConfigureAwait(false);
        }

        // Fail-fast guard: a CREATE VECTOR INDEX ... IF NOT EXISTS above is a no-op when the index
        // already exists, so an embedder/dimension change leaves stale indexes that would only fail at
        // query time. Verify dimensions now and surface an actionable error listing every mismatch.
        await ValidateVectorIndexDimensionsAsync(cancellationToken).ConfigureAwait(false);
        await ValidateNoFailedIndexesAsync(cancellationToken).ConfigureAwait(false);

        // Ordering matters and is the whole point: a fact written between an upgrade and the
        // backfill would MERGE onto a fresh node and duplicate anyway. Bootstrap runs before any
        // repository write, so this is the only place it is guaranteed to precede them.
        await BackfillCanonicalFactKeysAsync(CanonicalKeyBackfillBatchSize, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Schema bootstrap complete.");
    }

    /// <summary>
    /// Surfaces indexes that reached the terminal FAILED state. Only vector dimensions were checked
    /// before, so a range index that could not populate — Neo4j caps index keys at roughly 8 KB, and
    /// nothing bounds fact property length — degraded invisibly: queries still succeeded through full
    /// scans, so the symptom was gradual slowness rather than an error.
    /// </summary>
    private async Task ValidateNoFailedIndexesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var failed = await _txRunner.ReadAsync(
            async runner =>
            {
                var cursor = await runner.RunAsync(SchemaQueries.ShowIndexStates).ConfigureAwait(false);
                var records = await cursor.ToListAsync().ConfigureAwait(false);
                // Filtered in memory rather than in Cypher: a literal in a WHERE clause trips the
                // repository's parameterization guard, and POPULATING is a normal transient state.
                return records
                    .Where(record => string.Equals(
                        record["state"].As<string>(), "FAILED", StringComparison.OrdinalIgnoreCase))
                    .Select(record => $"{record["name"].As<string>()} ({record["type"].As<string>()})")
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false) ?? [];

        if (failed.Length > 0)
        {
            throw new InvalidOperationException(
                $"Neo4j reports {failed.Length} index(es) in the FAILED state: {string.Join(", ", failed)}. " +
                "A failed index does not stop queries — they fall back to full scans — so this would " +
                "otherwise surface only as unexplained slowness. Drop and recreate the index; if it " +
                "covers long text properties, note that Neo4j limits index keys to roughly 8 KB.");
        }
    }

    private async Task ValidateVectorIndexDimensionsAsync(CancellationToken cancellationToken)
    {
        if (!_validateVectorIndexDimensions)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var existing = await _txRunner.ReadAsync(
            async runner =>
            {
                var cursor = await runner.RunAsync(SchemaQueries.ShowVectorIndexDimensions).ConfigureAwait(false);
                var records = await cursor.ToListAsync().ConfigureAwait(false);
                return VectorIndexDimensionValidator.MapRows(records);
            },
            cancellationToken).ConfigureAwait(false) ?? [];

        VectorIndexDimensionValidator.EnsureMatches(_embeddingDimensions, existing);
        _logger.LogDebug(
            "Validated {Count} vector index(es) at {Dimensions} dimensions.",
            existing.Count, _embeddingDimensions);
    }

    private async Task RunStatementAsync(string cypher, CancellationToken cancellationToken)
    {
        try
        {
            await _txRunner.WriteAsync(
                async tx => { await tx.RunAsync(cypher).ConfigureAwait(false); },
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Executed schema statement: {Cypher}", cypher);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute schema statement: {Cypher}", cypher);
            throw;
        }
    }
}
