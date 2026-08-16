namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// The working-memory tier: a compiled per-owner profile block stored on upstream's own
/// <c>:User</c> identity node.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first extension whose parity delta REMOVES an upstream-only label.</b> Upstream defines a
/// first-class <c>:User</c> node that the .NET port deliberately never wrote — it sits in
/// <c>UpstreamOnlyLabels</c> with the note ".NET scopes via the owner_id property". Adopting it
/// <i>narrows</i> divergence rather than widening it: the label leaves the upstream-only list and
/// <c>NetOnlyLabels</c> stays empty. A new <c>:Profile</c> label would have cost an allowlist entry
/// and bought nothing.
/// </para>
/// <para>
/// <b>Keyed by <c>identifier</c>, which corrects the design.</b> The design proposed a new constraint
/// <c>user_owner_unique</c> on <c>owner_id</c> and told the implementer to check the upstream snapshot
/// before writing any identity property. The check says upstream's <c>:User</c> carries
/// <c>id, identifier, attributes</c> and is uniquely keyed on <b><c>identifier</c></b> by a constraint
/// named <c>user_identifier</c>. Adopting the label but keying it on a different property would have
/// made the adoption nominal — same spelling, different meaning, which is precisely the hazard the
/// parity verifier cannot catch because it compares names and not semantics. So this reuses upstream's
/// key and upstream's constraint name, and writes <c>owner_id</c> alongside it so .NET's own scoping
/// convention still reads naturally.
/// </para>
/// <para>
/// <b>Why the declarations live here and not in <c>SchemaConstants</c>.</b> Base constants are the
/// base schema — putting <c>:User</c> there would mean .NET claims the label even with the extension
/// off, and the parity report would simultaneously say the label is adopted and unimplemented.
/// Declared here, <c>EffectiveSchema</c> unions it in only while the extension is active.
/// </para>
/// </remarks>
internal sealed class WorkingMemorySchemaExtension : ISchemaExtension
{
    /// <summary>Upstream's own unique key for <c>:User</c>. We store the owner id in it.</summary>
    internal const string IdentifierProperty = "identifier";

    internal const string BlockProperty = "working_memory";
    internal const string BuiltAtProperty = "working_memory_built_at";
    internal const string HashProperty = "working_memory_hash";

    /// <summary>Upstream's constraint name, reused rather than reinvented.</summary>
    internal const string UniqueConstraintName = "user_identifier";

    internal const string UserLabel = "User";

    public string Id => "working-memory";

    public int Version => 1;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [UserLabel] = new HashSet<string>(
                [IdentifierProperty, BlockProperty, BuiltAtProperty, HashProperty],
                StringComparer.Ordinal),
        };

    public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <remarks>
    /// The one legal overlap: a label that exists upstream and not in .NET, adopted by pairing this
    /// declaration with the <c>RemoveUpstreamOnlyLabels</c> delta below. Without the pairing the
    /// validator refuses it as an undeclared label grab.
    /// </remarks>
    public IReadOnlySet<string> DeclaredLabels { get; } =
        new HashSet<string>([UserLabel], StringComparer.Ordinal);

    public IReadOnlyList<string> MigrationScripts { get; } = ["0001_user_profile.cypher"];

    public IReadOnlySet<string> BaseResidentMigrations { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public SchemaParityDelta ParityDelta { get; } = SchemaParityDelta.Create(
        removeUpstreamOnlyLabels: [UserLabel],
        addNetSupersetProperties: [BlockProperty, BuiltAtProperty, HashProperty],
        // Meeting proposal P3: names we ask upstream not to take for something else, since we are now
        // writing them onto a node upstream owns.
        reserveUpstreamPropertyNames: [BlockProperty, BuiltAtProperty, HashProperty]);

    public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);

    public TckProfileDescriptor TckProfile => TckProfileDescriptor.None;
}
