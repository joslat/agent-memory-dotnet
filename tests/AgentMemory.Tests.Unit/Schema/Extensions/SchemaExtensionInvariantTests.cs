using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Schema.Extensions;
using AgentMemory.Neo4j.Schema.Parity;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Schema.Extensions;

/// <summary>
/// <b>R1 — schema-additive-only.</b> An extension may add shapes; it may never rename, retype, or
/// repurpose a base one.
/// </summary>
/// <remarks>
/// Every check here is exercised against a deliberately-violating fake, because an invariant that has
/// only ever been shown passing is an invariant nobody has seen work. The real registered extensions
/// are then run through the same validator, so the rule and its subject share one implementation.
/// </remarks>
public sealed class SchemaExtensionInvariantTests
{
    private static readonly SchemaDescriptor Base = new(
        "net",
        NodeLabels: new HashSet<string>(["Entity", "Fact", "ReasoningTrace"], StringComparer.Ordinal),
        RelationshipTypes: new HashSet<string>(["HAS_FACT", "RELATED_TO"], StringComparer.Ordinal),
        Properties: new HashSet<string>(["id", "owner_id", "subject"], StringComparer.Ordinal));

    private static readonly SchemaParityPolicy Policy = SchemaParityPolicy.Upstream_0_5_0;

    private static IReadOnlyList<string> Validate(params ISchemaExtension[] extensions) =>
        SchemaExtensionValidator.ValidateDeclarations(extensions, Base, Policy);

    // ── the real ones ─────────────────────────────────────────────────────

    [Fact]
    public void EveryRegisteredExtensionSatisfiesR1AgainstTheRealBaseSchema()
    {
        var registry = new SchemaExtensionRegistry([new ProceduralSchemaExtension()]);

        SchemaExtensionValidator
            .ValidateDeclarations(registry.All, DotNetSchema.Describe(), SchemaParityPolicy.Upstream_0_5_0)
            .Should().BeEmpty();
    }

    [Fact]
    public void TheProceduralExtensionDeclaresOnlyAPropertyOnABaseLabel()
    {
        // The 0011 header's reasoning, pinned: promotion is a PROPERTY and not a :Procedure label or a
        // PROMOTED_FROM edge, because a new label or relationship type is parity-gated where a property
        // is not -- strictly more risk, zero more function. If a later edit adds either, this fails and
        // the parity conversation happens before the schema does.
        var procedural = new ProceduralSchemaExtension();

        procedural.DeclaredLabels.Should().BeEmpty();
        procedural.DeclaredRelationshipTypes.Should().BeEmpty();
        procedural.DeclaredProperties.Should().ContainKey("ReasoningTrace")
            .WhoseValue.Should().BeEquivalentTo(["trace_kind"]);
        procedural.BaseResidentMigrations.Should().BeEquivalentTo(["0011_trace_kind"]);
        procedural.MigrationScripts.Should().BeEmpty(
            "0011 is base-resident; re-declaring it under an ext key would record the same index twice");
    }

    // ── disjointness from base ────────────────────────────────────────────

    [Fact]
    public void DeclaringABaseRelationshipTypeIsRefused()
    {
        var problems = Validate(new FakeExtension("bad", relationshipTypes: ["HAS_FACT"]));

        problems.Should().ContainSingle().Which.Should().Contain("HAS_FACT").And.Contain("base already owns");
    }

    [Fact]
    public void DeclaringABaseLabelIsRefused()
    {
        var problems = Validate(new FakeExtension("bad", labels: ["Fact"]));

        problems.Should().ContainSingle().Which.Should().Contain("Fact").And.Contain("base already owns");
    }

    [Fact]
    public void AdoptingAnUpstreamOnlyLabelIsTheOneLegalOverlap()
    {
        // :User exists upstream and not in .NET. An extension may adopt it -- that is what
        // working-memory will do -- but ONLY by pairing the declaration with the parity delta that
        // says so. The pairing is what turns a label grab into a documented adoption.
        var adopting = new FakeExtension(
            "working-memory",
            labels: ["User"],
            delta: SchemaParityDelta.Create(removeUpstreamOnlyLabels: ["User"]));

        Validate(adopting).Should().BeEmpty();
    }

    [Fact]
    public void AStaleAdoptionIsRefused()
    {
        // Removing a label the base policy never listed as upstream-only means the delta describes a
        // world that no longer exists. Silence here would leave an allowlist entry nobody can trace.
        var problems = Validate(new FakeExtension(
            "bad", labels: ["Ghost"], delta: SchemaParityDelta.Create(removeUpstreamOnlyLabels: ["Ghost"])));

        problems.Should().ContainSingle().Which.Should().Contain("stale");
    }

    // ── mutual disjointness ───────────────────────────────────────────────

    [Fact]
    public void TwoExtensionsCannotOwnTheSameShape()
    {
        // Caught at build time rather than at merge time. A shape with two owners cannot be reported
        // to one, which would make the owners report ambiguous exactly where it matters.
        var problems = Validate(
            new FakeExtension("alpha", relationshipTypes: ["DERIVED_FROM"]),
            new FakeExtension("beta", relationshipTypes: ["DERIVED_FROM"]));

        problems.Should().ContainSingle().Which.Should().Contain("alpha").And.Contain("beta");
    }

    [Fact]
    public void TwoExtensionsCannotOwnTheSameLabelProperty()
    {
        var problems = Validate(
            new FakeExtension("alpha", properties: ("Fact", ["derivation"])),
            new FakeExtension("beta", properties: ("Fact", ["derivation"])));

        problems.Should().ContainSingle().Which.Should().Contain("prop:Fact.derivation");
    }

    [Fact]
    public void TheSamePropertyNameOnDifferentLabelsIsFine()
    {
        // Ownership is per (label, property), not per name. Two features writing a `reason` property on
        // two different labels are not in conflict, and forbidding it would push authors toward worse
        // names for no safety.
        Validate(
            new FakeExtension("alpha", properties: ("Fact", ["reason"])),
            new FakeExtension("beta", properties: ("Entity", ["reason"]))).Should().BeEmpty();
    }

    // ── identity ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Arithmetic")]
    [InlineData("delta_recall")]
    [InlineData("ext/nested")]
    [InlineData("")]
    public void AnIdThatIsNotLowercaseKebabIsRefused(string id)
    {
        // The id lands inside a (:Migration).version path key, so anything path-unsafe or
        // case-sensitive would produce two keys for one extension on two filesystems.
        SchemaExtensionValidator.ValidateIdentity(new FakeExtension(id))
            .Should().ContainSingle().Which.Should().Contain("lowercase-kebab");
    }

    [Fact]
    public void AMigrationScriptMustBeABareFilename()
    {
        // The ext/<id>/ prefix is the runner's to supply. A script that carries its own path would
        // record a version key the runner never looks for.
        SchemaExtensionValidator.ValidateIdentity(
                new FakeExtension("bad", scripts: ["ext/bad/0001_x.cypher"]))
            .Should().ContainSingle().Which.Should().Contain("bare");
    }

    [Fact]
    public void ADeclaredTckProfileRequiringNoCasesIsRefused()
    {
        SchemaExtensionValidator.ValidateIdentity(
                new FakeExtension("bad", tckProfile: new TckProfileDescriptor("Extensions/bad", 0)))
            .Should().ContainSingle().Which.Should().Contain("proves nothing");
    }

    [Fact]
    public void DuplicateIdsAreRefusedAtRegistration()
    {
        var register = () => new SchemaExtensionRegistry([new FakeExtension("dup"), new FakeExtension("dup")]);

        register.Should().Throw<SchemaInitializationException>().WithMessage("*Duplicate*dup*");
    }

    // ── the migration lint ────────────────────────────────────────────────

    [Theory]
    [InlineData("CREATE INDEX fact_derivation_key_idx IF NOT EXISTS FOR (f:Fact) ON (f.derivation_key);")]
    [InlineData("CREATE CONSTRAINT user_owner IF NOT EXISTS FOR (u:User) REQUIRE u.owner_id IS UNIQUE;")]
    [InlineData("MATCH (f:Fact) WHERE f.kind IS NULL SET f.kind = 'stated';")]
    [InlineData("// comment only\n")]
    public void AnAdditiveExtensionScriptPassesTheLint(string cypher) =>
        SchemaExtensionValidator.LintMigrationScript("arithmetic", "0001_x.cypher", cypher).Should().BeEmpty();

    [Theory]
    [InlineData("DROP INDEX trace_kind_idx;", "additive-only")]
    [InlineData("MATCH (f:Fact) REMOVE f.owner_id;", "additive-only")]
    [InlineData("MATCH (f:Fact) DETACH DELETE f;", "additive-only")]
    [InlineData("CREATE INDEX oops FOR (f:Fact) ON (f.x);", "IF NOT EXISTS")]
    [InlineData("CALL db.awaitIndexes();", "IF NOT EXISTS")]
    public void ADestructiveOrNonIdempotentExtensionScriptIsRefused(string cypher, string expected)
    {
        // This is what keeps future extension scripts additive without relying on review vigilance.
        // The BASE sequence is deliberately exempt: it is allowed to do things an optional module is
        // not, and it is reviewed on those terms.
        SchemaExtensionValidator.LintMigrationScript("arithmetic", "0001_x.cypher", cypher)
            .Should().ContainSingle().Which.Should().Contain(expected);
    }

    [Fact]
    public void EveryShippedExtensionScriptPassesItsOwnLint()
    {
        // Runs over whatever ext/ scripts exist next to the assembly, so the lint applies to the real
        // artifacts rather than only to the fakes above. Vacuous today (procedural is base-resident)
        // and the first thing that catches a bad script the day one ships.
        var root = Path.Combine(
            AppContext.BaseDirectory, "Schema", "Migrations",
            AgentMemory.Neo4j.Infrastructure.MigrationRunner.ExtensionFolder);
        if (!Directory.Exists(root)) return;

        foreach (var file in Directory.GetFiles(root, "*.cypher", SearchOption.AllDirectories))
        {
            var extensionId = Path.GetFileName(Path.GetDirectoryName(file))!;
            SchemaExtensionValidator
                .LintMigrationScript(extensionId, Path.GetFileName(file), File.ReadAllText(file))
                .Should().BeEmpty($"{file} must be additive and idempotent");
        }
    }

    /// <summary>A configurable extension used to drive each invariant into failure on purpose.</summary>
    private sealed class FakeExtension(
        string id,
        IEnumerable<string>? relationshipTypes = null,
        IEnumerable<string>? labels = null,
        (string Label, string[] Names)? properties = null,
        SchemaParityDelta? delta = null,
        IEnumerable<string>? scripts = null,
        IEnumerable<string>? dependsOn = null,
        TckProfileDescriptor? tckProfile = null) : ISchemaExtension
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

        public IReadOnlyList<string> MigrationScripts { get; } = [.. scripts ?? []];

        public IReadOnlySet<string> BaseResidentMigrations { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public SchemaParityDelta ParityDelta { get; } = delta ?? SchemaParityDelta.Empty;

        public IReadOnlySet<string> DependsOn { get; } =
            new HashSet<string>(dependsOn ?? [], StringComparer.Ordinal);

        public TckProfileDescriptor TckProfile { get; } = tckProfile ?? TckProfileDescriptor.None;
    }
}
