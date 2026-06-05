using Microsoft.Extensions.Logging;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Runs Cypher migration scripts in version order, tracking applied migrations
/// in a (:Migration {version, appliedAtUtc}) node.
/// </summary>
public sealed class MigrationRunner : IMigrationRunner
{
    private const string MigrationFolder = "Schema/Migrations";

    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly string _migrationsDirectory;

    public MigrationRunner(INeo4jTransactionRunner txRunner, ILogger<MigrationRunner> logger)
        : this(txRunner, logger, Path.Combine(AppContext.BaseDirectory, MigrationFolder))
    {
    }

    // Test seam: lets unit tests point the runner at a controlled directory (including one that
    // does not exist) instead of the shipped Schema/Migrations folder next to the assembly.
    internal MigrationRunner(
        INeo4jTransactionRunner txRunner,
        ILogger<MigrationRunner> logger,
        string migrationsDirectory)
    {
        _txRunner = txRunner;
        _logger = logger;
        _migrationsDirectory = migrationsDirectory;
    }

    public async Task RunMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var migrationFiles = DiscoverMigrations();

        if (migrationFiles.Count == 0)
        {
            _logger.LogInformation("No migration files found in {Folder}.", _migrationsDirectory);
            return;
        }

        await EnsureMigrationConstraintAsync(cancellationToken);

        foreach (var (version, filePath) in migrationFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsMigrationAppliedAsync(version, cancellationToken))
            {
                _logger.LogDebug("Migration {Version} already applied, skipping.", version);
                continue;
            }

            await ApplyMigrationAsync(version, filePath, cancellationToken);
        }
    }

    private List<(string Version, string FilePath)> DiscoverMigrations()
    {
        if (!Directory.Exists(_migrationsDirectory))
            return [];

        return Directory
            .GetFiles(_migrationsDirectory, "*.cypher")
            .OrderBy(f => Path.GetFileNameWithoutExtension(f))
            .Select(f => (Path.GetFileNameWithoutExtension(f), f))
            .ToList();
    }

    private async Task EnsureMigrationConstraintAsync(CancellationToken cancellationToken)
    {
        await _txRunner.WriteAsync(async tx => { await tx.RunAsync(SchemaQueries.MigrationVersionConstraint); }, cancellationToken);
    }

    private async Task<bool> IsMigrationAppliedAsync(string version, CancellationToken cancellationToken)
    {
        return await _txRunner.ReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                SchemaQueries.IsMigrationApplied,
                new { version });
            return await cursor.FetchAsync();
        }, cancellationToken);
    }

    private async Task ApplyMigrationAsync(string version, string filePath, CancellationToken cancellationToken)
    {
        var fileContent = await File.ReadAllTextAsync(filePath, cancellationToken);
        var statements = ParseStatements(fileContent);

        _logger.LogInformation(
            "Applying migration {Version} ({Count} statement(s)) from {File}.",
            version, statements.Count, filePath);

        // Run each statement in its OWN transaction. The Bolt protocol executes one statement
        // per RUN, and Neo4j forbids mixing schema operations (CREATE INDEX/CONSTRAINT) with the
        // data write that records the migration below. Every shipped statement is idempotent
        // (IF NOT EXISTS), so a partial apply is safely resumable on the next run.
        foreach (var statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _txRunner.WriteAsync(async tx => { await tx.RunAsync(statement); }, cancellationToken);
        }

        await _txRunner.WriteAsync(async tx =>
        {
            await tx.RunAsync(
                SchemaQueries.RecordMigration,
                new { version, appliedAtUtc = DateTime.UtcNow.ToString("O") });
        }, cancellationToken);

        _logger.LogInformation("Migration {Version} applied successfully.", version);
    }

    /// <summary>
    /// Splits a <c>.cypher</c> migration file into individual executable statements: strips
    /// <c>//</c> line comments, splits on <c>;</c>, trims, and drops blank fragments. Each
    /// resulting statement is run in its own transaction by <see cref="ApplyMigrationAsync"/>.
    /// </summary>
    internal static IReadOnlyList<string> ParseStatements(string cypher)
    {
        if (string.IsNullOrWhiteSpace(cypher))
            return [];

        // Strip whole-line and trailing `//` comments. Schema DDL contains no string literals
        // with `//`, so a line-level strip is sufficient and avoids a full Cypher tokenizer.
        var lines = cypher
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line =>
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            });

        var withoutComments = string.Join('\n', lines);

        return withoutComments
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }
}
