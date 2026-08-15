namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// A named, versioned, <b>additive-only</b> schema module: the "brain update" unit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The machinery that keeps the .NET port honest against upstream
/// <c>neo4j-agent-memory v0.5.0</c> had no concept of an optional module, and the consequences were
/// already concrete. Two independently-correct designs each claimed migration number <c>0012</c> as
/// "next free after 0011"; a database that enabled one and then the other would have had two scripts
/// fighting over a single key in the <c>(:Migration {version})</c> bookkeeping. And drift had no owner:
/// <c>trace_kind</c> shipped in migration 0011 with its rationale in a Cypher comment, while the parity
/// policy, the CLI and the docs knew nothing about which feature owned it.
/// </para>
/// <para>
/// <b>Activation.</b> Implementations are registered in DI <i>unconditionally</i> — the
/// <c>IMemoryReranker</c> precedent, where gating the registration on a flag meant a host that turned
/// the flag on through <c>IOptions</c> reconfiguration still got nothing. Activation is
/// <see cref="AgentMemory.Neo4j.Infrastructure.Neo4jOptions.Extensions"/>, a set of ids defaulting to
/// empty. <b>Empty set = today's base, byte-identical.</b>
/// </para>
/// <para>
/// <b>R1 — the one rule an implementation must not break.</b> An extension MUST NOT rename, retype, or
/// repurpose any base shape. It may only add. <see cref="SchemaExtensionValidator"/> enforces this at
/// build time over every registered implementation, active or not.
/// </para>
/// <para>
/// <b>Ids are frozen on first ship.</b> An id is load-bearing inside <c>(:Migration).version</c> keys
/// (<c>ext/&lt;id&gt;/000N_name</c>), so renaming one orphans applied migrations on every database that
/// has them.
/// </para>
/// <para>
/// Internal for the 1.x line. Making it public would SemVer-lock a surface still being learned;
/// promotion to a third-party extension point is a 2.0 decision.
/// </para>
/// </remarks>
internal interface ISchemaExtension
{
    /// <summary>Stable, lowercase-kebab id. Frozen on first ship — see the type remarks.</summary>
    string Id { get; }

    /// <summary>Monotonically increasing per-extension schema version.</summary>
    int Version { get; }

    /// <summary>Label → the property names this extension writes on it.</summary>
    /// <remarks>May target base labels (procedural writes <c>trace_kind</c> on <c>ReasoningTrace</c>).</remarks>
    IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; }

    /// <summary>Relationship types this extension introduces. Parity-gated, so keep it empty when possible.</summary>
    IReadOnlySet<string> DeclaredRelationshipTypes { get; }

    /// <summary>
    /// Node labels this extension introduces. Rare and expensive — a new label is parity-gated where a
    /// new property is not. Labels adopted <i>from</i> upstream are listed here too, paired with a
    /// <see cref="SchemaParityDelta.RemoveUpstreamOnlyLabels"/> entry.
    /// </summary>
    IReadOnlySet<string> DeclaredLabels { get; }

    /// <summary>
    /// Migration scripts, run in filename order <b>after</b> the whole base sequence. Each names a file
    /// shipped at <c>Schema/Migrations/ext/{Id}/000N_name.cypher</c>. May be empty — a
    /// properties-only extension needs no migration at all, and that is a legal shape rather than a
    /// missing piece.
    /// </summary>
    IReadOnlyList<string> MigrationScripts { get; }

    /// <summary>
    /// Base-sequence migration versions this extension <i>retro-owns</i>, e.g. procedural's
    /// <c>0011_trace_kind</c>.
    /// </summary>
    /// <remarks>
    /// Ownership only. These stay in the base sequence, run for everyone, and are never re-run under an
    /// <c>ext/</c> key — the point is that the owners report can answer "whose shape is this?" for
    /// schema that shipped before the extension system existed.
    /// </remarks>
    IReadOnlySet<string> BaseResidentMigrations { get; }

    /// <summary>The machine-checkable divergence this extension adds to (or removes from) the parity policy.</summary>
    SchemaParityDelta ParityDelta { get; }

    /// <summary>Extension ids that must be active for this one to work. Cycles are rejected at startup.</summary>
    IReadOnlySet<string> DependsOn { get; }

    /// <summary>
    /// The extension's TCK case profile, or <see cref="TckProfileDescriptor.None"/> when it has not
    /// shipped one yet.
    /// </summary>
    TckProfileDescriptor TckProfile { get; }
}

/// <summary>
/// The exact, machine-checkable change an extension makes to <see cref="Parity.SchemaParityPolicy"/>.
/// </summary>
/// <remarks>
/// Before this type, a parity change was a human instruction in a design document — "remove
/// <c>User</c> from <c>UpstreamOnlyLabels</c>, add 3 props" — with no machine link to the feature that
/// justified it and no way to un-edit it if the feature was abandoned. A delta is owned by its
/// extension and applies only while that extension is active.
/// </remarks>
/// <param name="AddNetOnlyLabels">.NET-only labels this extension introduces.</param>
/// <param name="AddNetOnlyRelationshipTypes">.NET-only relationship types this extension introduces.</param>
/// <param name="AddNetSupersetProperties">Properties .NET has that upstream does not.</param>
/// <param name="RemoveUpstreamOnlyLabels">
/// Upstream-only labels this extension adopts, e.g. working-memory adopting <c>:User</c>.
/// </param>
/// <param name="ReserveUpstreamPropertyNames">
/// Property names we ask upstream not to take for something else (meeting proposal P3).
/// </param>
internal sealed record SchemaParityDelta(
    IReadOnlySet<string> AddNetOnlyLabels,
    IReadOnlySet<string> AddNetOnlyRelationshipTypes,
    IReadOnlySet<string> AddNetSupersetProperties,
    IReadOnlySet<string> RemoveUpstreamOnlyLabels,
    IReadOnlySet<string> ReserveUpstreamPropertyNames)
{
    private static IReadOnlySet<string> Set(params string[] items) =>
        new HashSet<string>(items, StringComparer.Ordinal);

    /// <summary>A delta that changes nothing — the correct value for an extension with no parity impact.</summary>
    public static SchemaParityDelta Empty { get; } = new(Set(), Set(), Set(), Set(), Set());

    /// <summary>Builds a delta, defaulting every unspecified axis to empty.</summary>
    public static SchemaParityDelta Create(
        IEnumerable<string>? addNetOnlyLabels = null,
        IEnumerable<string>? addNetOnlyRelationshipTypes = null,
        IEnumerable<string>? addNetSupersetProperties = null,
        IEnumerable<string>? removeUpstreamOnlyLabels = null,
        IEnumerable<string>? reserveUpstreamPropertyNames = null) =>
        new(
            Set([.. addNetOnlyLabels ?? []]),
            Set([.. addNetOnlyRelationshipTypes ?? []]),
            Set([.. addNetSupersetProperties ?? []]),
            Set([.. removeUpstreamOnlyLabels ?? []]),
            Set([.. reserveUpstreamPropertyNames ?? []]));

    /// <summary>True when this delta asks for nothing at all.</summary>
    public bool IsEmpty =>
        AddNetOnlyLabels.Count == 0 && AddNetOnlyRelationshipTypes.Count == 0 &&
        AddNetSupersetProperties.Count == 0 && RemoveUpstreamOnlyLabels.Count == 0 &&
        ReserveUpstreamPropertyNames.Count == 0;
}

/// <summary>
/// Where an extension's TCK conformance cases live, and how many there must be.
/// </summary>
/// <remarks>
/// <see cref="None"/> is a real state, not a placeholder to be tolerated: the extension machinery
/// ships before most of its consumers do, and declaring a case folder that does not exist would be
/// precisely the ship-but-unreachable defect this codebase keeps catching. A declared profile is
/// checked for real by <c>SchemaExtensionReachabilityTests</c>.
/// </remarks>
internal sealed record TckProfileDescriptor(string CaseFolder, int MinimumCaseCount)
{
    /// <summary>No profile declared yet. The extension still runs under the base 178-case gate.</summary>
    public static TckProfileDescriptor None { get; } = new(string.Empty, 0);

    /// <summary>True when this extension has declared a case folder.</summary>
    public bool IsDeclared => CaseFolder.Length > 0;
}
