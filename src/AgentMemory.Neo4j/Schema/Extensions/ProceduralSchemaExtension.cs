using AgentMemory.Abstractions.Schema;

namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// Procedural memory: the <c>trace_kind</c> promotion marker that turns a reasoning trace into a
/// reusable procedure.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a retro-wrap, and that is the point.</b> Its schema already shipped, in base migration
/// <c>0011_trace_kind</c>, so activating this extension changes nothing about any database. It exists
/// for three reasons the extension system was built to serve: it proves the abstraction against a
/// feature that is already live rather than against one still being designed; it gives
/// <c>trace_kind</c> and <c>trace_kind_idx</c> an <b>owner</b>, so the <c>schema-check</c> owners
/// report can answer "whose shape is this?" for schema that predates the system; and it carries the
/// TCK profile slot for the promoted-trace conformance cases.
/// </para>
/// <para>
/// <b>Why <see cref="BaseResidentMigrations"/> rather than an <c>ext/procedural/0001</c> script.</b>
/// 0011 has already been applied to every database that ran migrations. Re-declaring it under an
/// extension key would replay an index creation under a second version key — harmless in effect
/// (<c>IF NOT EXISTS</c>) but a lie in the bookkeeping, and it would make the same physical index
/// appear twice in migration history. Ownership is recorded; the script stays exactly where it is.
/// </para>
/// <para>
/// <b>Parity.</b> <c>trace_kind</c> is a property, and properties are ungated by the parity verifier —
/// the delta below is documentary, which is why the 0011 header chose a property over a
/// <c>:Procedure</c> label or a <c>PROMOTED_FROM</c> edge in the first place: strictly more parity
/// risk, zero more function.
/// </para>
/// <para>
/// <b>R2 (write-path isolation).</b> <c>trace_kind</c> is written only by the promotion service; no
/// trace-repository create path sets it. <b>R3 (base-read neutrality).</b> Absent means
/// <c>'episode'</c> by <c>coalesce</c>, so a promoted trace is an ordinary trace on every read except
/// the prune's explicit exemption — which is 0011's whole reason for existing.
/// </para>
/// </remarks>
internal sealed class ProceduralSchemaExtension : ISchemaExtension
{
    /// <summary>The promotion marker. Named <c>trace_kind</c> and deliberately NOT <c>kind</c>.</summary>
    /// <remarks>
    /// <c>kind</c> already means "audit-node discriminator" both here and upstream, and overloading a
    /// property whose meaning is shared with another implementation is exactly the changed-semantics
    /// hazard the parity check cannot catch.
    /// </remarks>
    internal const string TraceKindProperty = "trace_kind";

    public string Id => "procedural";

    public int Version => 1;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [SchemaConstants.NodeLabels.ReasoningTrace] =
                new HashSet<string>([TraceKindProperty], StringComparer.Ordinal),
        };

    public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> DeclaredLabels { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    // None. See the type remarks: 0011 is base-resident and stays there.
    public IReadOnlyList<string> MigrationScripts { get; } = [];

    public IReadOnlySet<string> BaseResidentMigrations { get; } =
        new HashSet<string>(["0011_trace_kind"], StringComparer.Ordinal);

    public SchemaParityDelta ParityDelta { get; } =
        SchemaParityDelta.Create(addNetSupersetProperties: [TraceKindProperty]);

    public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);

    // No case folder yet. The extension machinery ships ahead of the TCK profile convention it
    // defines, and declaring a folder that does not exist would be the ship-but-unreachable defect
    // this codebase keeps catching -- asserted rather than assumed by the reachability guard.
    public TckProfileDescriptor TckProfile => TckProfileDescriptor.None;
}
