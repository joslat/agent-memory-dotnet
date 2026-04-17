using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Runs Cypher migration scripts in version order, tracking applied migrations
/// in a (:Migration {version, appliedAtUtc}) node.
/// </summary>
public sealed class MigrationRunner : IMigrationRunner
{
    private const string MigrationFolder = "Schema/Migrations";

    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(INeo4jTransactionRunner txRunner, ILogger<MigrationRunner> logger)
    {
        _txRunner = txRunner;
        _logger = logger;
    }

    public async Task RunMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var migrationFiles = DiscoverMigrations();

        if (migrationFiles.Count == 0)
        {
            _logger.LogInformation("No migration files found in {Folder}.", MigrationFolder);
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

    private static List<(string Version, string FilePath)> DiscoverMigrations()
    {
        var baseDir = AppContext.BaseDirectory;
        var migrationDir = Path.Combine(baseDir, MigrationFolder);

        if (!Directory.Exists(migrationDir))
            return [];

        return Directory
            .GetFiles(migrationDir, "*.cypher")
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
        var cypher = await File.ReadAllTextAsync(filePath, cancellationToken);

        _logger.LogInformation("Applying migration {Version} from {File}.", version, filePath);

        await _txRunner.WriteAsync(async tx =>
        {
            await tx.RunAsync(cypher);
            await tx.RunAsync(
                SchemaQueries.RecordMigration,
                new { version, appliedAtUtc = DateTime.UtcNow.ToString("O") });
        }, cancellationToken);

        _logger.LogInformation("Migration {Version} applied successfully.", version);
    }
}
