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
/// <b>Where the scripts come from.</b> They are embedded in this assembly at build time
/// (<see cref="EmbeddedMigrationScripts"/>), so every installed application runs them however the package reached it; a
/// folder is only the tests' seam (<see cref="DirectoryMigrationScripts"/>).
/// </para>
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
    /// <summary>The subfolder, under the scripts' root, that holds per-extension namespaces.</summary>
    internal const string ExtensionFolder = "ext";

    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly MigrationScripts _scripts;
    private readonly IReadOnlyList<ISchemaExtension> _activeExtensions;

    public MigrationRunner(
        INeo4jTransactionRunner txRunner,
        ILogger<MigrationRunner> logger,
        SchemaExtensionRegistry registry,
        IOptions<Neo4jOptions> options)
        : this(txRunner, logger, EmbeddedMigrationScripts.Shipped, ResolveActive(registry, options))
    {
    }

    // Test seam: lets unit tests point the runner at a controlled directory (including one that
    // does not exist) instead of the shipped Schema/Migrations folder next to the assembly.
    internal MigrationRunner(
        INeo4jTransactionRunner txRunner,
        ILogger<MigrationRunner> logger,
        string migrationsDirectory,
        IReadOnlyList<ISchemaExtension>? activeExtensions = null)
        : this(txRunner, logger, new DirectoryMigrationScripts(migrationsDirectory), activeExtensions)
    {
    }

    internal MigrationRunner(
        INeo4jTransactionRunner txRunner,
        ILogger<MigrationRunner> logger,
        MigrationScripts scripts,
        IReadOnlyList<ISchemaExtension>? activeExtensions = null)
    {
        _txRunner = txRunner;
        _logger = logger;
        _scripts = scripts;
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
            // A warning, not information: no scripts means no schema, which is what NuGet consumers of 2.1.0 and of the
            // 2.2.0 meta-package got in silence (the scripts never reached their build).
            _logger.LogWarning("No migration scripts found in {Location}; the schema migrations will not be applied.",
                _scripts.Location);
            return;
        }

        await EnsureMigrationConstraintAsync(cancellationToken).ConfigureAwait(false);

        // REV2-7, REV3-7: every shipped statement is independent and idempotent (IF NOT EXISTS, or a backfill that sets a
        // missing value), so a statement the database refuses (a unique constraint over duplicates) stops nothing else:
        // the other statements of its script, and every later script, still run. A script is recorded only when all of
        // its statements applied, so the next start tries it again. The first failure is rethrown at the end.
        Neo4jException? first = null;
        foreach (var (version, name, extensionId) in migrationFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsMigrationAppliedAsync(version, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("Migration {Version} already applied, skipping.", version);
                continue;
            }

            var refused = await ApplyMigrationAsync(version, name, extensionId, cancellationToken).ConfigureAwait(false);
            first ??= refused;
        }
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
    }

    /// <summary>
    /// The full ordered plan: every base script, then every active extension's scripts in registry
    /// (topological) order.
    /// </summary>
    /// <remarks>
    /// Internal so a test can assert the ORDER without a database. Order is the part that matters and
    /// the part a live test would prove slowest and least clearly.
    /// </remarks>
    internal List<(string Version, string Name, string? ExtensionId)> DiscoverMigrations()
    {
        var plan = new List<(string, string, string?)>();

        // Base first, always; a base name has no folder, so ext/ is never swept up with it.
        plan.AddRange(_scripts.Base().Select(name => (Strip(name), name, (string?)null)));

        foreach (var extension in _activeExtensions)
        {
            var scripts = _scripts.Extension(extension.Id);
            if (scripts.Count == 0)
            {
                if (extension.MigrationScripts.Count > 0)
                {
                    // Declared scripts that are not there is a packaging fault, not a configuration one:
                    // the extension believes it has schema and the database will never get it. Loud,
                    // because the failure would otherwise show up as a missing index much later.
                    _logger.LogWarning(
                        "Schema extension {Extension} declares {Count} migration script(s) but {Location} has none "
                        + "for it; its schema will NOT be applied.",
                        extension.Id, extension.MigrationScripts.Count, _scripts.Location);
                }

                continue;
            }

            plan.AddRange(scripts.Select(name => (Strip(name), name, (string?)extension.Id)));
        }

        return plan;

        // The recorded version is the name without ".cypher": "0002_x" or "ext/<id>/0001_x", as before.
        static string Strip(string name) => name[..^".cypher".Length];
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

    /// <returns>The first statement the database refused, if any; the script is then not recorded.</returns>
    private async Task<Neo4jException?> ApplyMigrationAsync(
        string version, string name, string? extensionId, CancellationToken cancellationToken)
    {
        var script = await _scripts.ReadAsync(name, cancellationToken).ConfigureAwait(false);
        var statements = ParseStatements(script);

        _logger.LogInformation(
            "Applying migration {Version} ({Count} statement(s)) from {Location}.",
            version, statements.Count, _scripts.Location);

        // Run each statement in its OWN transaction. The Bolt protocol executes one statement
        // per RUN, and Neo4j forbids mixing schema operations (CREATE INDEX/CONSTRAINT) with the
        // data write that records the migration below. Every shipped statement is idempotent
        // (IF NOT EXISTS), so a partial apply is safely resumable on the next run.
        Neo4jException? refused = null;
        foreach (var statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _txRunner.WriteAsync(async tx => { await tx.RunAsync(statement).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
            }
            catch (Neo4jException exception)
            {
                _logger.LogError(exception, "A statement of migration {Version} could not be applied ({Message}); the script is not recorded " +
                    "and is tried again at the next start.", version, exception.Message);
                refused ??= exception;
            }
        }
        if (refused is not null) return refused;

        await _txRunner.WriteAsync(async tx =>
        {
            await tx.RunAsync(
                SchemaQueries.RecordMigration,
                new { version, appliedAtUtc = DateTime.UtcNow.ToString("O"), extensionId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Migration {Version} applied successfully.", version);
        return null;
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
