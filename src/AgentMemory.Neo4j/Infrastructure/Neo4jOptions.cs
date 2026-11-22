namespace AgentMemory.Neo4j.Infrastructure;

public class Neo4jOptions
{
    public string Uri { get; set; } = "bolt://localhost:7687";
    public string Username { get; set; } = "neo4j";
    public string Password { get; set; } = "password";
    public string Database { get; set; } = "neo4j";
    public int MaxConnectionPoolSize { get; set; } = 100;
    public TimeSpan ConnectionAcquisitionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the driver tries to open one connection before it gives up. <see langword="null"/> (default) keeps
    /// the driver's own default, and the driver is configured exactly as before.
    /// </summary>
    /// <remarks>
    /// For a host that must fail fast when the store is unreachable, such as a voice turn with a deadline in hundreds
    /// of milliseconds. With <see cref="MaxTransactionRetryTime"/> it bounds how long a call keeps working after its
    /// caller stopped waiting.
    /// </remarks>
    public TimeSpan? ConnectionTimeout { get; set; }

    /// <summary>
    /// How long a managed transaction keeps retrying transient failures, such as an unreachable or restarting server.
    /// <see langword="null"/> (default) keeps the driver's own default (30 seconds), and the driver is configured
    /// exactly as before; <see cref="TimeSpan.Zero"/> means one attempt.
    /// </summary>
    /// <remarks>
    /// The driver's transaction functions take no <see cref="CancellationToken"/>, so a retry loop keeps running after
    /// the caller's deadline has passed and the caller has moved on. This is the setting that bounds that loop.
    /// </remarks>
    public TimeSpan? MaxTransactionRetryTime { get; set; }

    /// <summary>
    /// DRT-4 (C-08). A pooled connection unused for longer than this is tested (one round trip) before it is used, so the
    /// first call after a quiet spell finds a connection a firewall or the server dropped and opens a new one, instead of
    /// failing after the driver's 30 seconds. Tested by hand: 34 ms instead of a failure. Default 2 minutes;
    /// <see langword="null"/> leaves the driver's own default (no test).
    /// </summary>
    public TimeSpan? ConnectionLivenessCheckTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// DRT-4 (C-08). A pooled connection idle for longer than this is closed. Set it below a server's or a firewall's
    /// known idle limit. Default not set (the driver keeps idle connections).
    /// </summary>
    public TimeSpan? ConnectionIdleTimeout { get; set; }

    /// <summary>
    /// Encrypts the connection for a plain <c>bolt://</c> or <c>neo4j://</c> URI. A URI whose scheme sets encryption
    /// itself (<c>neo4j+s://</c>, <c>neo4j+ssc://</c>, <c>bolt+s://</c>, <c>bolt+ssc://</c>) is used as it says, and this
    /// option is not applied to it.
    /// </summary>
    public bool EncryptionEnabled { get; set; } = false;

    /// <summary>
    /// Server-side deadline for every managed read/write transaction. <see langword="null"/> (default)
    /// means no deadline, which is today's behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing bounds a query today. The driver's <c>ExecuteReadAsync</c>/<c>ExecuteWriteAsync</c> take
    /// no <see cref="CancellationToken"/>, so an in-flight query cannot be interrupted from this side —
    /// cooperative cancellation only checks <em>before</em> work starts. A transaction timeout is the
    /// only mechanism that actually bounds one, because the <b>server</b> enforces it.
    /// </para>
    /// <para>
    /// This matters more since the owner-starvation rescue: a scoped search that returns nothing falls
    /// back to a scan bounded by one owner's data, and "one owner's data" is unbounded in principle.
    /// </para>
    /// <para>
    /// <b>Default is null on purpose.</b> A timeout that is too low converts a slow query into a failed
    /// one, and the right value depends on deployment shape, so this is a deliberate operator decision
    /// rather than a number chosen here. Values must be strictly positive; zero or negative is rejected
    /// at startup rather than silently meaning "no timeout".
    /// </para>
    /// </remarks>
    public TimeSpan? TransactionTimeout { get; set; }

    /// <summary>
    /// Dimensionality of embedding vectors. Must match the model used to generate embeddings.
    /// Default is 1536 (OpenAI text-embedding-ada-002 / text-embedding-3-small).
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>
    /// Persists a message batch, its embeddings, ordering links, and read-back in one Cypher query.
    /// Enabled by default; disable only for compatibility diagnosis or controlled A/B measurement.
    /// The legacy path remains available and preserves its original multi-query behavior.
    /// </summary>
    public bool UseOptimizedMessageBatchWrites { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (the default), schema bootstrap verifies that every existing vector
    /// index was created with <see cref="EmbeddingDimensions"/> and throws
    /// <see cref="AgentMemory.Abstractions.Exceptions.EmbeddingDimensionMismatchException"/> if any
    /// differ. This is a fail-fast guard against switching embedding models without recreating the
    /// indexes (which would otherwise surface as an opaque query-time error). Set to
    /// <see langword="false"/> to skip the check (e.g. during an intentional, staged migration).
    /// </summary>
    public bool ValidateVectorIndexDimensions { get; set; } = true;

    /// <summary>
    /// G-16. Owner-filtered vector indexes, for Neo4j 2026.x and later (default false). A vector index there can carry
    /// filter properties and filter inside the index, so an owner's search is never crowded out of a global top-K by
    /// other owners' rows (the starvation <c>RescueShortOwnerResults</c> and the owner-first scans work around on 5.26).
    /// On: bootstrap creates <c>fact_embedding_owner_idx</c> and <c>entity_embedding_owner_idx</c> (the embedding,
    /// filtered on <c>owner_key</c>) and refuses to start on an older server; owner-scoped fact and entity recall
    /// searches them (own rows, then shared rows, merged by score) instead of the global index. Off, nothing changes.
    /// </summary>
    public bool FilteredVectorIndexes { get; set; }

    /// <summary>
    /// When <see langword="true"/> (the default), schema bootstrap also applies the migration scripts: the base sequence,
    /// then the scripts of every active schema extension (<see cref="Extensions"/>, identity's among them where
    /// evidence-based identity runs). Each script runs once per database and is recorded in a <c>(:Migration)</c> node,
    /// so later starts only check. Set <see langword="false"/> where an operator owns the database's schema and applies
    /// the scripts with <see cref="IMigrationRunner"/> (or the repository's <c>agentmemory migrate</c>) instead.
    /// </summary>
    /// <remarks>
    /// Before 2.3.0 nothing in the library ran the scripts, so an installed application never got an extension's schema:
    /// identity ran without its unique constraints and lookup indexes. A script the database refuses (for example a unique
    /// constraint over nodes that were already duplicated) is logged as an error and bootstrap completes as before; the
    /// next start tries again.
    /// </remarks>
    public bool ApplyMigrationsOnBootstrap { get; set; } = true;

    /// <summary>
    /// DRT-2. Bootstrap first lists the constraints and indexes the store has (one read) and sends only the statements
    /// whose object is missing, and the migration runner reads which scripts are applied once instead of once per
    /// script. Every statement is <c>IF NOT EXISTS</c>, so the ones skipped would have changed nothing: the result is the
    /// same, by fewer round trips (start-up on an existing store: 280 waited at 20 ms away, measured by PERF-D-03). A
    /// listing that fails runs every statement, as before. <see langword="false"/> is the previous path exactly, for
    /// measuring both on one build.
    /// </summary>
    public bool BootstrapOnlyMissingSchema { get; set; } = true;

    /// <summary>
    /// Ids of the optional schema extensions this deployment activates. Empty (the default) is today's
    /// base schema, byte-identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A schema extension is a named, versioned, additive-only module — its own migration namespace
    /// (<c>ext/&lt;id&gt;/000N</c>), its own parity delta, its own owner entry in the
    /// <c>schema-check</c> owners report. Implementations are registered unconditionally and consulted
    /// through this set, the <c>IMemoryReranker</c> pattern: gating the registration itself would mean
    /// a host that enabled an extension through <c>IOptions</c> reconfiguration still got nothing.
    /// </para>
    /// <para>
    /// An unknown id is <b>rejected at startup</b>, listing the known ones, rather than ignored. A
    /// deployment that asked for an extension and silently ran without it is the failure this whole
    /// mechanism exists to make impossible.
    /// </para>
    /// <para>
    /// A mutable <see cref="ISet{T}"/> rather than an init-only collection, so it is reachable from a
    /// <c>configureNeo4j</c> lambda and from configuration binding — the issue-#100 lesson, where
    /// init-only option records could not be configured at all.
    /// </para>
    /// </remarks>
    public ISet<string> Extensions { get; set; } = new HashSet<string>(StringComparer.Ordinal);
}
