using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Schema.Extensions;
using AgentMemory.Neo4j.Schema.Parity;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Schema.Extensions;

/// <summary>
/// Composing the effective schema and the effective parity policy from base plus active extensions.
/// </summary>
/// <remarks>
/// <para>
/// The <b>byte-identical-when-off</b> guarantee lives here, at the descriptor level: with nothing
/// active, both compositions must be the base values themselves. Everything downstream —
/// <c>SchemaParityVerifier</c>, the CLI, the TCK bridge — reads through these two functions, so if they
/// are identities when off, the whole system is.
/// </para>
/// <para>
/// <c>SchemaParityVerifier.Verify</c> is deliberately untouched by any of this. It already took the
/// descriptor and the policy as parameters, so the comparison algorithm never had to learn what an
/// extension is.
/// </para>
/// </remarks>
public sealed class SchemaExtensionParityCompositionTests
{
    private static readonly SchemaDescriptor Base = new(
        "net",
        NodeLabels: new HashSet<string>(["Entity", "Fact"], StringComparer.Ordinal),
        RelationshipTypes: new HashSet<string>(["HAS_FACT"], StringComparer.Ordinal),
        Properties: new HashSet<string>(["id", "owner_id"], StringComparer.Ordinal));

    // ── EffectiveSchema ───────────────────────────────────────────────────

    [Fact]
    public void WithNothingActiveTheEffectiveSchemaIsTheBaseDescriptorItself()
    {
        // Reference equality on purpose. Rebuilding would produce an EQUAL descriptor today and would
        // quietly stop doing so the first time a union step gained a normalisation the base path lacks
        // -- which is exactly how an "identical when off" claim rots without anyone noticing.
        EffectiveSchema.Describe(Base, []).Should().BeSameAs(Base);
    }

    [Fact]
    public void TheLiveEffectiveSchemaWithNothingActiveEqualsTheLiveBaseSchema()
    {
        var live = DotNetSchema.Describe();

        EffectiveSchema.Describe([]).Should().BeEquivalentTo(live);
    }

    [Fact]
    public void ActiveDeclarationsAreUnionedIn()
    {
        var extension = new StubExtension(
            "arithmetic",
            relationshipTypes: ["DERIVED_FROM"],
            properties: ("Fact", ["derivation_key", "derived_at"]));

        var effective = EffectiveSchema.Describe(Base, [extension]);

        effective.RelationshipTypes.Should().BeEquivalentTo(["HAS_FACT", "DERIVED_FROM"]);
        effective.Properties.Should().BeEquivalentTo(["id", "owner_id", "derivation_key", "derived_at"]);
        effective.NodeLabels.Should().BeEquivalentTo(Base.NodeLabels);
    }

    [Fact]
    public void ComposingDoesNotMutateTheBaseDescriptor()
    {
        var extension = new StubExtension("arithmetic", relationshipTypes: ["DERIVED_FROM"]);

        EffectiveSchema.Describe(Base, [extension]);

        Base.RelationshipTypes.Should().BeEquivalentTo(["HAS_FACT"]);
    }

    // ── SchemaParityPolicy.WithExtensions ─────────────────────────────────

    [Fact]
    public void WithNothingActiveThePolicyIsItself()
    {
        var policy = SchemaParityPolicy.Upstream_0_5_0;

        policy.WithExtensions([]).Should().BeSameAs(policy);
    }

    [Fact]
    public void AdditionsAreUnionedIntoTheAllowlists()
    {
        var effective = SchemaParityPolicy.Upstream_0_5_0.WithExtensions([
            new StubExtension("arithmetic", delta: SchemaParityDelta.Create(
                addNetOnlyRelationshipTypes: ["DERIVED_FROM"],
                addNetSupersetProperties: ["derivation_key"])),
        ]);

        effective.NetOnlyRelationshipTypes.Should().Contain("DERIVED_FROM")
            .And.Contain("HAS_FACT", "base entries survive composition");
        effective.NetSupersetProperties.Should().Contain("derivation_key").And.Contain("owner_id");
    }

    [Fact]
    public void AdoptingAnUpstreamOnlyLabelRemovesItFromTheUpstreamOnlyList()
    {
        // The working-memory case. Before this, adopting :User was a hand-edit to a static allowlist
        // with no machine link to the feature that justified it and no way to undo it if the feature
        // was dropped.
        var effective = SchemaParityPolicy.Upstream_0_5_0.WithExtensions([
            new StubExtension("working-memory", delta: SchemaParityDelta.Create(
                removeUpstreamOnlyLabels: ["User"])),
        ]);

        effective.UpstreamOnlyLabels.Should().NotContain("User");
        SchemaParityPolicy.Upstream_0_5_0.UpstreamOnlyLabels.Should().Contain("User",
            "composition is a pure function; the shared static policy must be untouched");
    }

    [Fact]
    public void AStaleRemovalThrowsRatherThanPassingSilently()
    {
        var compose = () => SchemaParityPolicy.Upstream_0_5_0.WithExtensions([
            new StubExtension("bad", delta: SchemaParityDelta.Create(removeUpstreamOnlyLabels: ["Ghost"])),
        ]);

        compose.Should().Throw<SchemaInitializationException>().WithMessage("*stale*");
    }

    [Fact]
    public void AnAdditionCollidingWithBaseThrows()
    {
        // R1 re-checked at composition time, because only here is the effective combination known.
        var compose = () => SchemaParityPolicy.Upstream_0_5_0.WithExtensions([
            new StubExtension("bad", delta: SchemaParityDelta.Create(
                addNetOnlyRelationshipTypes: ["HAS_FACT"])),
        ]);

        compose.Should().Throw<SchemaInitializationException>().WithMessage("*already allows*");
    }

    [Fact]
    public void TwoExtensionsAddingTheSameRelationshipTypeThrow()
    {
        var compose = () => SchemaParityPolicy.Upstream_0_5_0.WithExtensions([
            new StubExtension("alpha", delta: SchemaParityDelta.Create(addNetOnlyRelationshipTypes: ["X"])),
            new StubExtension("beta", delta: SchemaParityDelta.Create(addNetOnlyRelationshipTypes: ["X"])),
        ]);

        compose.Should().Throw<SchemaInitializationException>().WithMessage("*beta*already allows*");
    }

    [Fact]
    public void TwoExtensionsMayShareASupersetPropertyDeclaration()
    {
        // Properties are additive and unowned at the parity layer -- two features can legitimately both
        // declare that .NET has a scope property upstream lacks. Only labels and relationship types are
        // gated, so only they can collide.
        var effective = SchemaParityPolicy.Upstream_0_5_0.WithExtensions([
            new StubExtension("alpha", delta: SchemaParityDelta.Create(addNetSupersetProperties: ["shared"])),
            new StubExtension("beta", delta: SchemaParityDelta.Create(addNetSupersetProperties: ["shared"])),
        ]);

        effective.NetSupersetProperties.Should().Contain("shared");
    }

    // ── the two worlds stay green ─────────────────────────────────────────

    [Fact]
    public void TheLiveSchemaVerifiesUnderBothTheBaseAndTheEffectivePolicy()
    {
        // The standing claim of the extension system: turning the procedural retro-wrap on changes
        // nothing a parity verifier can see. If it ever does, that is a real parity event and it should
        // be argued for, not discovered.
        var registry = new SchemaExtensionRegistry([new ProceduralSchemaExtension()]);
        var upstream = new UpstreamSchemaRegistry().Load("0.5.0");
        var basePolicy = SchemaParityPolicy.Upstream_0_5_0;
        var active = registry.Active(["procedural"]);

        var baseReport = SchemaParityVerifier.Verify(DotNetSchema.Describe(), upstream, basePolicy);
        var effectiveReport = SchemaParityVerifier.Verify(
            EffectiveSchema.Describe(active), upstream, basePolicy.WithExtensions(active));

        baseReport.IsCompatible.Should().BeTrue();
        effectiveReport.IsCompatible.Should().BeTrue();
        effectiveReport.MissingLabels.Should().BeEquivalentTo(baseReport.MissingLabels);
        effectiveReport.UndocumentedNetOnlyLabels.Should().BeEquivalentTo(baseReport.UndocumentedNetOnlyLabels);
        effectiveReport.UndocumentedNetOnlyRelationshipTypes
            .Should().BeEquivalentTo(baseReport.UndocumentedNetOnlyRelationshipTypes);
    }

    private sealed class StubExtension(
        string id,
        IEnumerable<string>? relationshipTypes = null,
        IEnumerable<string>? labels = null,
        (string Label, string[] Names)? properties = null,
        SchemaParityDelta? delta = null) : ISchemaExtension
    {
        public string Id => id;
        public int Version => 1;

        public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
            properties is null
                ? new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    [properties.Value.Label] = new HashSet<string>(properties.Value.Names, StringComparer.Ordinal),
                };

        public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
            new HashSet<string>(relationshipTypes ?? [], StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredLabels { get; } =
            new HashSet<string>(labels ?? [], StringComparer.Ordinal);

        public IReadOnlyList<string> MigrationScripts { get; } = [];

        public IReadOnlySet<string> BaseResidentMigrations { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public SchemaParityDelta ParityDelta { get; } = delta ?? SchemaParityDelta.Empty;
        public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);
        public TckProfileDescriptor TckProfile { get; } = TckProfileDescriptor.None;
    }
}
