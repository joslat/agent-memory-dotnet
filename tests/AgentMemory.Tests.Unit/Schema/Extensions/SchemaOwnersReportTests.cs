using AgentMemory.Neo4j.Schema.Extensions;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Schema.Extensions;

/// <summary>
/// "Whose shape is this?" — the question <see cref="AgentMemory.Neo4j.Schema.Parity.SchemaParityVerifier"/>
/// cannot answer.
/// </summary>
/// <remarks>
/// <c>trace_kind</c> shipped in migration 0011 with its entire rationale in a Cypher comment. The
/// parity policy allowed it, the CLI never mentioned it, and nothing anywhere recorded which feature
/// it belonged to. One such property was survivable; the five extensions this system was built for
/// would not have been.
/// </remarks>
public sealed class SchemaOwnersReportTests
{
    private static SchemaExtensionRegistry Registry() => new([new ProceduralSchemaExtension()]);

    private static Dictionary<string, string?> NoMigrations() => new(StringComparer.Ordinal);

    [Fact]
    public void EveryShapeTheShippedExtensionDeclaresNamesIt()
    {
        var report = SchemaOwnersReport.Build(Registry(), ["procedural"], NoMigrations());

        report.HasOwners.Should().BeTrue();
        report.Shapes.Should().Contain(s => s.Kind == "property" && s.Name == "ReasoningTrace.trace_kind"
            && s.Owner == "procedural");
        report.Shapes.Should().Contain(s => s.Kind == "migration" && s.Name == "0011_trace_kind"
            && s.Owner == "procedural");
    }

    [Fact]
    public void AnInactiveExtensionIsStillDescribedAndMarkedInactive()
    {
        // Deactivation is not a down-migration: the schema stays (additive and harmless), so a report
        // that described only active extensions would stop naming an owner exactly when someone is
        // trying to work out where a leftover shape came from.
        var report = SchemaOwnersReport.Build(Registry(), [], NoMigrations());

        report.HasOwners.Should().BeTrue();
        report.Active.Should().BeEmpty();
        report.Shapes.Should().Contain(s => s.Owner == "procedural" && !s.IsActive
            && s.Name == "ReasoningTrace.trace_kind");
    }

    [Fact]
    public void AnAppliedExtensionMigrationIsAttributedToItsExtension()
    {
        var report = SchemaOwnersReport.Build(
            Registry(),
            ["procedural"],
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["0011_trace_kind"] = "2026-08-01T00:00:00Z",
                ["ext/procedural/0001_example"] = "2026-08-20T10:00:00Z",
            });

        report.HasOwners.Should().BeTrue();
        report.AppliedExtensionMigrations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Version = "ext/procedural/0001_example",
                Owner = "procedural",
                AppliedAtUtc = "2026-08-20T10:00:00Z",
                OwnerIsRegistered = true,
            });
    }

    [Fact]
    public void BaseMigrationsAreNotListedAsExtensionMigrations()
    {
        var report = SchemaOwnersReport.Build(
            Registry(),
            [],
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["0002_owner_scope"] = "2026-01-01T00:00:00Z",
                ["0011_trace_kind"] = "2026-01-01T00:00:00Z",
            });

        report.AppliedExtensionMigrations.Should().BeEmpty();
        report.HasOwners.Should().BeTrue();
    }

    [Fact]
    public void AnAppliedMigrationFromAnUnregisteredExtensionIsAnOrphan()
    {
        // THE live failure this catches: the database carries schema applied by a module this binary
        // does not have -- a downgrade, or an extension someone removed. Silence here would leave
        // indexes and properties in the graph that nothing can account for.
        var report = SchemaOwnersReport.Build(
            Registry(),
            [],
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ext/arithmetic/0001_derived_fact"] = "2026-08-20T10:00:00Z",
            });

        report.HasOwners.Should().BeFalse();
        report.Orphans.Should().ContainSingle().Which.Should()
            .Contain("arithmetic").And.Contain("not registered");
        report.Render().Should().Contain("FAILED").And.Contain("no owner");
    }

    [Fact]
    public void ADivergenceWithNoDeclarationIsAnOrphan()
    {
        // The build-time half. An extension whose parity delta allows a relationship type it never
        // declared has grown an allowlist entry nobody can attribute -- the delta and the declarations
        // have drifted apart, and the effective policy is quietly wider than any feature justifies.
        var undeclaring = new DeltaOnlyExtension(
            "loose", SchemaParityDelta.Create(addNetOnlyRelationshipTypes: ["SOME_NEW_REL"]));

        var report = SchemaOwnersReport.Build(
            new SchemaExtensionRegistry([undeclaring]), ["loose"], NoMigrations());

        report.HasOwners.Should().BeFalse();
        report.Orphans.Should().ContainSingle().Which.Should()
            .Contain("SOME_NEW_REL").And.Contain("declared by no active extension");
    }

    [Fact]
    public void ADeclaredDivergenceIsNotAnOrphan()
    {
        var declaring = new DeltaOnlyExtension(
            "tight",
            SchemaParityDelta.Create(addNetOnlyRelationshipTypes: ["DERIVED_FROM"]),
            relationshipTypes: ["DERIVED_FROM"]);

        SchemaOwnersReport.Build(new SchemaExtensionRegistry([declaring]), ["tight"], NoMigrations())
            .HasOwners.Should().BeTrue();
    }

    [Fact]
    public void TheHeaderNamesThePolicyAndTheActiveExtensionsWithVersions()
    {
        SchemaOwnersReport.Build(Registry(), ["procedural"], NoMigrations()).Render()
            .Should().Contain("policy base 0.5.0").And.Contain("[procedural v1]");
    }

    [Fact]
    public void TheHeaderSaysNoneWhenNothingIsActive()
    {
        SchemaOwnersReport.Build(Registry(), [], NoMigrations()).Render()
            .Should().Contain("extensions: [none]");
    }

    private sealed class DeltaOnlyExtension(
        string id, SchemaParityDelta delta, IEnumerable<string>? relationshipTypes = null) : ISchemaExtension
    {
        public string Id => id;
        public int Version => 1;

        public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
            new HashSet<string>(relationshipTypes ?? [], StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredLabels { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<string> MigrationScripts { get; } = [];

        public IReadOnlySet<string> BaseResidentMigrations { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public SchemaParityDelta ParityDelta { get; } = delta;
        public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);
        public TckProfileDescriptor TckProfile { get; } = TckProfileDescriptor.None;
    }
}
