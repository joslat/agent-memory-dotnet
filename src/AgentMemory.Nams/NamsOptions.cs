namespace AgentMemory.Nams;

/// <summary>
/// Configuration for the NAMS (Neo4j Agent Memory as a Service) backend. Deliberately a plain class, not
/// a <c>record</c>: a record's compiler-generated <c>ToString()</c> would print every property -- including
/// <see cref="ApiKey"/> -- verbatim; a plain class inherits <see cref="object.ToString"/> instead, so this
/// type structurally cannot leak its own secret through logging/diagnostics that call <c>ToString()</c> on
/// it without anyone having to remember to override anything.
/// </summary>
/// <remarks>
/// This is a configuration-surface-only phase (no HTTP client, no NAMS calls exist yet). Credentials held
/// directly on a long-lived options object is a known, disclosed trade-off for now -- a token-provider
/// abstraction is real future work once the client adapter (a later phase) exists to consume it, not a
/// blocker for this phase.
/// </remarks>
public sealed class NamsOptions
{
    /// <summary>The NAMS REST API base endpoint, e.g. <c>https://memory.neo4jlabs.com/v1</c>.</summary>
    public required Uri Endpoint { get; set; }

    /// <summary>
    /// The NAMS API key (<c>nams_...</c>) or bearer token used for <c>Authorization: Bearer &lt;token&gt;</c>.
    /// Never included in <see cref="ToString"/>, logs, or validation error messages.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The NAMS workspace to operate within. Optional: workspace can also resolve from the API key itself
    /// per the NAMS REST API reference; set this explicitly only when a host's key spans multiple workspaces.
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>Per-request timeout for calls to NAMS. Must be positive.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of retry attempts for a retryable (idempotent) NAMS call. Must be non-negative.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Initial backoff delay before the first retry. Must be non-negative.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How a persistence call behaves on failure. See <see cref="NamsPersistenceFailureMode"/>.</summary>
    public NamsPersistenceFailureMode PersistenceFailureMode { get; set; } = NamsPersistenceFailureMode.BestEffort;

    /// <summary>
    /// Allows <see cref="Endpoint"/> to use a non-HTTPS scheme (e.g. <c>http://localhost:...</c>) for local
    /// development against a self-hosted NAMS-compatible sandbox. Defaults to <see langword="false"/> --
    /// production configuration must use HTTPS.
    /// </summary>
    public bool AllowInsecureEndpointForLocalDevelopment { get; set; }
}
