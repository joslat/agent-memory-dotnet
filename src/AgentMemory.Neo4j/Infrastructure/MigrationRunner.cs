using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Schema.Extensions;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Runs Cypher migration scripts in version order, tracking applied migrations
/// in a (:Migration {version, appliedAtUtc, extension_id}) node.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two namespaces, one runner.</b> The base sequence lives at <c>Schema/Migrations/000N_name.cypher</c>
/// and runs for everyone, always, first. Each <i>active</i> schema extension then runs its own scripts
/// from <c>Schema/Migrations/ext/&lt;id&gt;/000N_name.cypher</c>, recorded under the namespaced version
/// key <c>ext/&lt;id&gt;/000N_name</c>.
/// </para>
/// <para>
/// <b>Why a namespace rather than a longer linear sequence.</b> The linear sequence cannot host optional
/// modules: two independently-written extensions each correctly claimed <c>0012</c> as "next free after
/// 0011", and a database enabling one and later the other would have had two different scripts fighting
/// over a single key in the unique-constrained <c>(:Migration {version})</c> bookkeeping. Namespaced
/// keys cannot collide with base names (a base name never contains <c>/</c>), so the existing unique
/// constraint keeps covering everything and no new constraint is needed.
/// </para>
/// <para>
/// <b>Base-first is absolute.</b> A database that enabled extension A at base 0011 and later upgrades to
/// a library shipping 0012 and 0013 replays those, then re-reaches A's scripts and skips them through
/// the same applied-check — no conflict, because the namespaces never share a key and each namespace is
/// internally linear. Cross-namespace order between two independent extensions is irrelevant: R1 makes
/// both purely additive over base and forbids either from touching the other's shapes.
/// </para>
/// </remarks>
internal sealed class MigrationRunner : IMigrationRunner
{
    private const string MigrationFolder = "Schema/Migrations";

    /// <summary>The subfolder under <see cref="MigrationFolder"/> that holds per-extension namespaces.</summary>
    internal const string ExtensionFolder = "ext";

    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly string _migrationsDirectory;
    private readonly IReadOnlyList<ISchemaExtension> _activeExtensions;

    public MigrationRunner(
        INeo4jTransactionRunner txRunner,
        ILogger<MigrationRunner> logger,
        SchemaExtensionRegistry registry,
        IOptions<Neo4jOptions> options)
        : this(
            txRunner,
            logger,
            Path.Combine(AppContext.BaseDirectory, MigrationFolder),
            ResolveActive(registry, options))
    {
    }

    // Test seam: lets unit tests point the runner at a controlled directory (including one that
    // does not exist) instead of the shipped Schema/Migrations folder next to the assembly.
    internal MigrationRunner(
        INeo4jTransactionRunner txRunner,
        ILogger<MigrationRunner> logger,
        string migrationsDirectory,
        IReadOnlyList<ISchemaExtension>? activeExtensions = null)
    {
        _txRunner = txRunner;
        _logger = logger;
        _migrationsDirectory = migrationsDirectory;
        _activeExtensions = activeExtensions ?? [];
    }

    private static IReadOnlyList<ISchemaExtension> ResolveActive(
        SchemaExtensionRegistry registry, IOptions<Neo4jOptions> options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        return registry.Active(options.Value);
    }

    public async Task RunMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var migrationFiles = DiscoverMigrations();

        if (migrationFiles.Count == 0)
        {
            _logger.LogInformation("No migration files found in {Folder}.", _migrationsDirectory);
            return;
        }

        await EnsureMigrationConstraintAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (version, filePath, extensionId) in migrationFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsMigrationAppliedAsync(version, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("Migration {Version} already applied, skipping.", version);
                continue;
            }

            await ApplyMigrationAsync(version, filePath, extensionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The full ordered plan: every base script, then every active extension's scripts in registry
    /// (topological) order.
    /// </summary>
    /// <remarks>
    /// Internal so a test can assert the ORDER without a database. Order is the part that matters and
    /// the part a live test would prove slowest and least clearly.
    /// </remarks>
    internal List<(string Version, string FilePath, string? ExtensionId)> DiscoverMigrations()
    {
        var plan = new List<(string, string, string?)>();

        if (Directory.Exists(_migrationsDirectory))
        {
            // Base first, always. Note the top-level-only enumeration: ext/ is a subdirectory, so it
            // is not swept up here even though it lives underneath.
            plan.AddRange(Directory
                .GetFiles(_migrationsDirectory, "*.cypher", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
                .Select(file => (Path.GetFileNameWithoutExtension(file), file, (string?)null)));
        }

        foreach (var extension in _activeExtensions)
        {
            var directory = Path.Combine(_migrationsDirectory, ExtensionFolder, extension.Id);
            if (!Directory.Exists(directory))
            {
                if (extension.MigrationScripts.Count > 0)
                {
                    // Declared scripts with no folder is a packaging fault, not a configuration one:
                    // the extension believes it has schema and the database will never get it. Loud,
                    // because the failure would otherwise show up as a missing index much later.
                    _logger.LogWarning(
                        "Schema extension {Extension} declares {Count} migration script(s) but "
                        + "{Directory} does not exist; its schema will NOT be applied.",
                        extension.Id, extension.MigrationScripts.Count, directory);
                }

                continue;
            }

            plan.AddRange(Directory
                .GetFiles(directory, "*.cypher", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
                .Select(file => (
                    $"{ExtensionFolder}/{extension.Id}/{Path.GetFileNameWithoutExtension(file)}",
                    file,
                    (string?)extension.Id)));
        }

        return plan;
    }

    private async Task EnsureMigrationConstraintAsync(CancellationToken cancellationToken)
    {
        await _txRunner.WriteAsync(async tx => { await tx.RunAsync(SchemaQueries.MigrationVersionConstraint).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsMigrationAppliedAsync(string version, CancellationToken cancellationToken)
    {
        return await _txRunner.ReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                SchemaQueries.IsMigrationApplied,
                new { version }).ConfigureAwait(false);
            return await cursor.FetchAsync().ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyMigrationAsync(
        string version, string filePath, string? extensionId, CancellationToken cancellationToken)
    {
        var fileContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
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
            await _txRunner.WriteAsync(async tx => { await tx.RunAsync(statement).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
        }

        await _txRunner.WriteAsync(async tx =>
        {
            await tx.RunAsync(
                SchemaQueries.RecordMigration,
                new { version, appliedAtUtc = DateTime.UtcNow.ToString("O"), extensionId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

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
