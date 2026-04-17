using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public sealed class SchemaBootstrapper : ISchemaBootstrapper
{
    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<SchemaBootstrapper> _logger;
    private readonly string[] _vectorIndexes;

    public SchemaBootstrapper(
        INeo4jTransactionRunner txRunner,
        IOptions<Neo4jOptions> options,
        ILogger<SchemaBootstrapper> logger)
    {
        _txRunner = txRunner;
        _logger = logger;

        var dims = options.Value.EmbeddingDimensions;
        _vectorIndexes = SchemaQueries.BuildVectorIndexes(dims);
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

        _logger.LogInformation("Schema bootstrap complete.");
    }

    /// <summary>
    /// Delegates to <see cref="SchemaQueries.BuildVectorIndexes"/> for backward compatibility.
    /// </summary>
    public static string[] BuildVectorIndexes(int dimensions) =>
        SchemaQueries.BuildVectorIndexes(dimensions);

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
