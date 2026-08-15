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

    /// <summary>
    /// This policy plus the divergences declared by the <b>active</b> schema extensions — a pure
    /// function returning a new record, leaving the shared static policy untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The allowlists above used to be edited by hand when a feature needed a divergence, with no
    /// machine link between the edit and the feature that justified it and no way to un-edit it if the
    /// feature was dropped. A delta is owned by its extension and applies only while that extension is
    /// active, so "which feature is this divergence for?" has an answer the code can give.
    /// </para>
    /// <para>
    /// Two validations happen here rather than at declaration time, because only here is the effective
    /// combination known: a removal naming a label this policy never listed as upstream-only is a
    /// <b>stale delta</b> and throws loudly, and an addition colliding with an already-allowed entry is
    /// R1 re-checked at composition time. Silence on either would let the policy grow entries nobody
    /// can trace.
    /// </para>
    /// </remarks>
    public SchemaParityPolicy WithExtensions(IEnumerable<Extensions.ISchemaExtension> active)
    {
        ArgumentNullException.ThrowIfNull(active);

        var extensions = active.ToList();
        if (extensions.Count == 0) return this;

        var netOnlyLabels = new HashSet<string>(NetOnlyLabels, StringComparer.Ordinal);
        var netOnlyRelationshipTypes = new HashSet<string>(NetOnlyRelationshipTypes, StringComparer.Ordinal);
        var netSupersetProperties = new HashSet<string>(NetSupersetProperties, StringComparer.Ordinal);
        var upstreamOnlyLabels = new HashSet<string>(UpstreamOnlyLabels, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var extension in extensions)
        {
            var delta = extension.ParityDelta;

            foreach (var label in delta.RemoveUpstreamOnlyLabels)
            {
                if (!upstreamOnlyLabels.Remove(label))
                {
                    problems.Add(
                        $"extension '{extension.Id}' removes upstream-only label '{label}', which this "
                        + "policy does not list. The delta is stale — the label was already adopted, or "
                        + "it never existed upstream.");
                }
            }

            Add(problems, netOnlyLabels, delta.AddNetOnlyLabels, extension.Id, "net-only label");
            Add(problems, netOnlyRelationshipTypes, delta.AddNetOnlyRelationshipTypes, extension.Id,
                "net-only relationship type");
            // Superset PROPERTIES are additive and may legitimately be declared by more than one
            // extension (two features can both write `owner_id`-style scope), so a duplicate here is
            // not a collision the way a label or relationship type is.
            netSupersetProperties.UnionWith(delta.AddNetSupersetProperties);
        }

        if (problems.Count > 0)
        {
            throw new AgentMemory.Abstractions.Exceptions.SchemaInitializationException(
                "Schema-extension parity delta(s) cannot be applied:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)),
                "schema-extension-parity");
        }

        return this with
        {
            UpstreamOnlyLabels = upstreamOnlyLabels,
            NetOnlyLabels = netOnlyLabels,
            NetOnlyRelationshipTypes = netOnlyRelationshipTypes,
            NetSupersetProperties = netSupersetProperties,
        };
    }

    private static void Add(
        List<string> problems, HashSet<string> target, IReadOnlySet<string> additions,
        string extensionId, string kind)
    {
        foreach (var addition in additions)
        {
            if (!target.Add(addition))
            {
                problems.Add(
                    $"extension '{extensionId}' adds {kind} '{addition}', which the effective policy "
                    + "already allows. Either base owns it or another active extension does — a shape "
                    + "with two owners cannot be reported to one.");
            }
        }
    }
}
