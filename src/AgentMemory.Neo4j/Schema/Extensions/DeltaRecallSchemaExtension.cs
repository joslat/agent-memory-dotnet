namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// Delta recall: "give me only what changed since my last checkpoint".
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes only — no labels, no relationship types, no properties.</b> The whole feature is read
/// over clocks that already exist and are already enforced on the live path: <c>created_at</c> stamped
/// on create only, <c>invalidated_at</c> stamped idempotently, <c>SUPERSEDED_BY</c> edges, and the
/// valid-time window. The extension exists to make those clocks <i>seekable</i>, not to add anything
/// to remember.
/// </para>
/// <para>
/// <b>Parity delta: empty.</b> An index is invisible to the parity verifier by construction, which is
/// also why the TCK audit classifies this Gold-safe with the flag ON: an index changes plans, never
/// results, and the new repository members are called by no bridge endpoint.
/// </para>
/// <para>
/// <b>The checkpoint is a caller-held token, not a stored node</b>, which is why this extension has no
/// <c>:MemoryCheckpoint</c> label. A stored checkpoint would be a real parity allowlist entry for a
/// need no host has yet; the API shape (<c>Since</c> on the request, <c>TakenAtUtc</c> on the
/// response) is designed so that can be added later without changing it.
/// </para>
/// </remarks>
internal sealed class DeltaRecallSchemaExtension : ISchemaExtension
{
    public string Id => "delta-recall";

    public int Version => 1;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> DeclaredLabels { get; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyList<string> MigrationScripts { get; } = ["0001_clock_indexes.cypher"];

    public IReadOnlySet<string> BaseResidentMigrations { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public SchemaParityDelta ParityDelta => SchemaParityDelta.Empty;

    public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);

    public TckProfileDescriptor TckProfile => TckProfileDescriptor.None;
}
