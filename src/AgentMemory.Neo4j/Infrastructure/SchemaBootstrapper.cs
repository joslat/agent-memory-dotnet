using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

public sealed class SchemaBootstrapper : ISchemaBootstrapper
{
    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<SchemaBootstrapper> _logger;
    private readonly string[] _vectorIndexes;
    private readonly int _embeddingDimensions;
    private readonly bool _validateVectorIndexDimensions;

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
            await RunStatementAsync(constraint, cancellationToken);
        }

        foreach (var index in SchemaQueries.FulltextIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken);
        }

        foreach (var index in _vectorIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken);
        }

        foreach (var index in SchemaQueries.PropertyIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken);
        }

        // Fail-fast guard: a CREATE VECTOR INDEX ... IF NOT EXISTS above is a no-op when the index
        // already exists, so an embedder/dimension change leaves stale indexes that would only fail at
        // query time. Verify dimensions now and surface an actionable error listing every mismatch.
        await ValidateVectorIndexDimensionsAsync(cancellationToken);

        _logger.LogInformation("Schema bootstrap complete.");
    }

    private async Task ValidateVectorIndexDimensionsAsync(CancellationToken cancellationToken)
    {
        if (!_validateVectorIndexDimensions)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var existing = await _txRunner.ReadAsync(
            async runner =>
            {
                var cursor = await runner.RunAsync(SchemaQueries.ShowVectorIndexDimensions);
                var records = await cursor.ToListAsync();
                return VectorIndexDimensionValidator.MapRows(records);
            },
            cancellationToken) ?? [];

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
                async tx => { await tx.RunAsync(cypher); },
                cancellationToken);

            _logger.LogDebug("Executed schema statement: {Cypher}", cypher);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute schema statement: {Cypher}", cypher);
            throw;
        }
    }
}
