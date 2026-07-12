namespace AgentMemory.Neo4j.Schema.Parity;

/// <summary>
/// The set of <b>intentional, documented divergences</b> between the .NET schema and a specific upstream
/// version — the only deltas <see cref="SchemaParityVerifier"/> treats as acceptable. Anything outside
/// these allowlists (a dropped label, a renamed property, a brand-new undocumented divergence, or an
/// upstream version that caught up to a .NET superset) is reported as a compatibility break.
/// </summary>
internal sealed record SchemaParityPolicy(
    string UpstreamVersion,
    IReadOnlySet<string> UpstreamOnlyLabels,
    IReadOnlySet<string> NetOnlyLabels,
    IReadOnlySet<string> NetOnlyRelationshipTypes,
    IReadOnlySet<string> NetSupersetProperties,
    IReadOnlySet<string> UpstreamOnlyProperties,
    IReadOnlySet<string> InteropCriticalProperties)
{
    private static IReadOnlySet<string> Set(params string[] items) =>
        new HashSet<string>(items, StringComparer.Ordinal);

    /// <summary>The documented divergence policy for upstream <c>neo4j-agent-memory v0.5.0</c>.</summary>
    public static SchemaParityPolicy Upstream_0_5_0 { get; } = new(
        UpstreamVersion: "0.5.0",
        // Upstream labels the .NET port intentionally does not implement.
        UpstreamOnlyLabels: Set(
            "User"),           // upstream's first-class identity node; .NET scopes via the owner_id property
        // .NET-only labels (none today — a new one must be added here deliberately).
        NetOnlyLabels: Set(),
        // .NET-only relationship extensions (documented in SchemaConstants.RelationshipTypes).
        NetOnlyRelationshipTypes: Set("HAS_FACT", "HAS_PREFERENCE", "IN_SESSION"),
        // .NET property supersets absent upstream (owner scope, transaction-time clock, and read-audit detail).
        NetSupersetProperties: Set("owner_id", "owner_key", "invalidated_at", "last_accessed_at", "access_count", "memory_id", "read_at"),
        // Upstream properties the .NET port intentionally does not model as SchemaConstants (so the
        // structural property gate doesn't flag them as missing). Anything NOT on this list that exists
        // upstream but vanishes from .NET is a break — which is exactly how a silent rename is caught.
        UpstreamOnlyProperties: Set(
            "actions_taken", "archived", "archived_at", "attributes", "candidate_count", "config",
            "created_by", "dry_run", "error_kind", "extraction_time_ms", "identifier", "is_active",
            "ran_at", "recorded_at", "version"),
        // Property names that MUST be spelled identically on both sides (cross-impl read contract).
        InteropCriticalProperties: Set(
            "id", "name", "type", "embedding", "confidence", "metadata",
            "created_at", "updated_at", "timestamp", "session_id",
            "role", "content", "canonical_name", "aliases", "merged_into", "merged_at",
            "subject", "predicate", "object", "valid_from", "valid_until", "category", "preference",
            "task", "task_embedding", "started_at", "completed_at",
            "step_number", "action", "observation", "thought",
            "tool_name", "status", "duration_ms"));

    private static readonly IReadOnlyDictionary<string, SchemaParityPolicy> ByVersion =
        new Dictionary<string, SchemaParityPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["0.5.0"] = Upstream_0_5_0,
        };

    /// <summary>Versions for which a documented divergence policy exists.</summary>
    public static IReadOnlyCollection<string> KnownVersions => (IReadOnlyCollection<string>)ByVersion.Keys;

    /// <summary>Returns the documented policy for <paramref name="upstreamVersion"/>, or throws if none is registered.</summary>
    public static SchemaParityPolicy ForVersion(string upstreamVersion) =>
        ByVersion.TryGetValue(upstreamVersion, out var policy)
            ? policy
            : throw new KeyNotFoundException(
                $"No schema-parity policy registered for upstream version '{upstreamVersion}'. Known: {string.Join(", ", ByVersion.Keys)}.");
}
