using AgentMemory.Abstractions.Schema;

namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// Arithmetic (derived) memory: the session accountant's materialised aggregates.
/// </summary>
/// <remarks>
/// <para>
/// <b>No new label.</b> A derived fact is an ordinary <c>:Fact</c> carrying <c>kind='derived'</c>. A
/// <c>:DerivedFact</c> label would have cost a label allowlist entry <i>and</i> forfeited free recall,
/// since every fact query matches <c>:Fact</c> — strictly more parity risk for strictly less function.
/// </para>
/// <para>
/// <b>One new relationship type, and it is argued for rather than assumed.</b> The two parity-free
/// alternatives both fail on something specific. Reusing <c>EXTRACTED_FROM</c> is wrong twice over: it
/// points at <c>:Message</c>, not <c>:Fact</c>, and it would poison the provenance instrument with edges
/// that are not extraction provenance at all. A JSON property listing input fact ids is parity-free but
/// <b>not traversable in the direction that matters</b> — the staleness cascade needs "every derived
/// fact whose inputs include the fact being superseded" <i>inside</i> the supersede statement, which is
/// a full label scan per supersession with a JSON list and one relationship expansion with an edge. The
/// cascade is the safety property of the whole feature, so it gets the edge.
/// </para>
/// <para>
/// <b>R2 (write-path isolation).</b> Derived facts are written only by <c>UpsertDerivedAsync</c>, which
/// no upstream-parity surface calls. <b>R3 (base-read neutrality).</b> A derived fact <i>is</i> a fact
/// and is meant to be recallable — that is not a violation but the design — while the group read that
/// feeds the accountant excludes them by <c>kind</c>, keeping the derivation DAG one level deep.
/// </para>
/// <para>
/// <b>The cascade is unconditional, not gated on this extension.</b> If the accountant is switched off
/// while derived facts exist, staleness protection must survive the flag; otherwise turning the feature
/// off would freeze every aggregate it ever wrote into permanent truth.
/// </para>
/// </remarks>
internal sealed class ArithmeticSchemaExtension : ISchemaExtension
{
    /// <summary>Marks a fact as computed rather than observed.</summary>
    internal const string KindProperty = "kind";

    /// <summary>Recompute-in-place identity: one node per subject × predicate × operator × owner.</summary>
    internal const string DerivationKeyProperty = "derivation_key";

    internal const string DerivationOperatorProperty = "derivation_operator";
    internal const string DerivationProperty = "derivation";
    internal const string DerivedAtProperty = "derived_at";

    /// <summary>Derived fact → the facts it was computed from.</summary>
    internal const string DerivedFromRelationship = "DERIVED_FROM";

    public string Id => "arithmetic";

    public int Version => 1;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [SchemaConstants.NodeLabels.Fact] = new HashSet<string>(
                [
                    KindProperty, DerivationKeyProperty, DerivationOperatorProperty,
                    DerivationProperty, DerivedAtProperty,
                ],
                StringComparer.Ordinal),
        };

    public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
        new HashSet<string>([DerivedFromRelationship], StringComparer.Ordinal);

    public IReadOnlySet<string> DeclaredLabels { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyList<string> MigrationScripts { get; } = ["0001_derived_fact.cypher"];

    public IReadOnlySet<string> BaseResidentMigrations { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public SchemaParityDelta ParityDelta { get; } = SchemaParityDelta.Create(
        addNetOnlyRelationshipTypes: [DerivedFromRelationship],
        addNetSupersetProperties:
        [
            KindProperty, DerivationKeyProperty, DerivationOperatorProperty,
            DerivationProperty, DerivedAtProperty,
        ]);

    public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);

    public TckProfileDescriptor TckProfile => TckProfileDescriptor.None;
}
