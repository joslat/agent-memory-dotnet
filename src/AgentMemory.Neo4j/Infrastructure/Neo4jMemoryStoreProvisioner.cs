using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Provisions a Neo4j database per application store and bootstraps its schema (R1b,
/// <see cref="MemoryStorageStrategy.DatabasePerApplication"/>). Requires Neo4j Enterprise or AuraDB —
/// Community Edition supports only a single user database and raises an actionable error.
/// </summary>
internal sealed class Neo4jMemoryStoreProvisioner : IMemoryStoreProvisioner
{
    private const string SystemDatabase = "system";

    private readonly INeo4jSessionFactory _sessionFactory;
    private readonly MemoryStoreOptions _storeOptions;
    private readonly int _embeddingDimensions;
    private readonly bool _validateVectorIndexDimensions;
    private readonly ILogger<Neo4jMemoryStoreProvisioner> _logger;

    // Provisioned (database-name) set, so repeat requests for the same store skip the round-trip.
    private readonly ConcurrentDictionary<string, byte> _provisioned = new(StringComparer.Ordinal);

    public Neo4jMemoryStoreProvisioner(
        INeo4jSessionFactory sessionFactory,
        IOptions<Neo4jOptions> neo4jOptions,
        IOptions<MemoryStoreOptions> storeOptions,
        ILogger<Neo4jMemoryStoreProvisioner> logger)
    {
        _sessionFactory = sessionFactory;
        _storeOptions = storeOptions.Value;
        _embeddingDimensions = neo4jOptions.Value.EmbeddingDimensions;
        _validateVectorIndexDimensions = neo4jOptions.Value.ValidateVectorIndexDimensions;
        _logger = logger;
    }

    public async Task EnsureStoreAsync(string? applicationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // SharedDatabase (or no application id) uses the default database, which is assumed to exist
        // and be bootstrapped by SchemaBootstrapper at startup — nothing to provision.
        if (_storeOptions.Strategy != MemoryStorageStrategy.DatabasePerApplication
            || string.IsNullOrWhiteSpace(applicationId))
        {
            return;
        }

        var fallback = _storeOptions.DefaultDatabase;
        var database = StoreDatabaseNaming.Resolve(_storeOptions, fallback, applicationId);

        if (_provisioned.ContainsKey(database))
            return;

        if (!_storeOptions.AutoProvision)
        {
            // The operator manages database lifecycle out-of-band; trust that it exists.
            _provisioned.TryAdd(database, 0);
            return;
        }

        await CreateDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        await BootstrapDatabaseAsync(database, cancellationToken).ConfigureAwait(false);

        _provisioned.TryAdd(database, 0);
        _logger.LogInformation("Provisioned memory store database '{Database}' for application '{App}'.",
            database, applicationId);
    }

    private async Task CreateDatabaseAsync(string database, CancellationToken cancellationToken)
    {
        // The only method in this file that was missing this, while EnsureStoreAsync,
        // BootstrapDatabaseAsync and ValidateVectorIndexDimensionsAsync all had it -- and the one where
        // it matters most, because WAIT below blocks until the database is online. A caller who
        // cancelled got no cancellation and no indication that the token was being ignored.
        cancellationToken.ThrowIfCancellationRequested();

        // CREATE DATABASE is an administrative command and must run against the system database.
        // WAIT blocks until the database is online, so the subsequent bootstrap can connect.
        // The name must be backtick-quoted: StoreDatabaseNaming yields legal-charset names, but the
        // default 'mem-' prefix contains a dash, and unquoted dashes are a Cypher syntax error.
        var session = _sessionFactory.OpenSession(SystemDatabase, AccessMode.Write);
        await using var _ = session.ConfigureAwait(false); // ConfigureAwait the disposal without rebinding session's type
        try
        {
            await session.ExecuteWriteAsync(tx => tx.RunAsync($"CREATE DATABASE {QuoteName(database)} IF NOT EXISTS WAIT")).ConfigureAwait(false);
        }
        catch (Neo4jException ex) when (IsMultiDatabaseUnsupported(ex))
        {
            throw new NotSupportedException(
                "MemoryStorageStrategy.DatabasePerApplication requires Neo4j Enterprise Edition or AuraDB; " +
                "Community Edition supports only a single database. Use MemoryStorageStrategy.SharedDatabase " +
                "(isolate users via owner_id), or run a separate Neo4j instance/connection per application.",
                ex);
        }
    }

    private async Task BootstrapDatabaseAsync(string database, CancellationToken cancellationToken)
    {
        // Each schema statement runs in its own transaction against the freshly created database.
        // All statements are idempotent (IF NOT EXISTS), so re-provisioning is safe.
        var statements = SchemaQueries.BootstrapStatements(_embeddingDimensions);
        var session = _sessionFactory.OpenSession(database, AccessMode.Write);
        await using var _ = session.ConfigureAwait(false); // ConfigureAwait the disposal without rebinding session's type
        foreach (var statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await session.ExecuteWriteAsync(tx => tx.RunAsync(statement)).ConfigureAwait(false);
        }

        await ValidateVectorIndexDimensionsAsync(session, cancellationToken).ConfigureAwait(false);
    }

    // Mirrors SchemaBootstrapper's guard for the shared database: a per-app database that already
    // existed (re-provision after an embedder change) keeps its stale vector indexes, since
    // CREATE ... IF NOT EXISTS is a no-op. Fail-fast with the offending indexes listed.
    private async Task ValidateVectorIndexDimensionsAsync(
        IAsyncSession session, CancellationToken cancellationToken)
    {
        if (!_validateVectorIndexDimensions)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var existing = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(SchemaQueries.ShowVectorIndexDimensions).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return VectorIndexDimensionValidator.MapRows(records);
        }).ConfigureAwait(false) ?? [];

        VectorIndexDimensionValidator.EnsureMatches(_embeddingDimensions, existing);
    }

    // Backtick-quotes a Neo4j object name for use in an administrative Cypher command, doubling any
    // embedded backticks. Required because database names can contain dashes/dots (e.g. the 'mem-'
    // prefix) which are illegal unquoted.
    private static string QuoteName(string name) => "`" + name.Replace("`", "``") + "`";

    private static bool IsMultiDatabaseUnsupported(Neo4jException ex)
    {
        var code = ex.Code ?? string.Empty;
        var message = ex.Message ?? string.Empty;
        return code.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
            || message.Contains("administration command", StringComparison.OrdinalIgnoreCase);
    }
}
