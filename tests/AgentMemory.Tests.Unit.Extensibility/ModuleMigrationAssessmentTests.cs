using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleMigrationAssessmentTests
{
    private static ModuleResolution Resolve() => ModuleResolver.Resolve([new ModuleManifest
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example.reviews",
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new ModuleOntology { LabelPrefix = "Review", Types = [new() { Name = "ReviewItem" }] }
    }], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });

    private static ModuleDatabaseSnapshot Snapshot(IEnumerable<ModuleNamespaceClaim> claims, params ModuleDatabaseObject[] objects) => new(
        ModuleMigrationAssessment.BaseConstraints.Select(name => new ModuleDatabaseObject(name, "UNIQUENESS", "NODE", [], [], true, null, null))
            .Concat(objects).ToArray(), new HashSet<string>(), new HashSet<string>(),
        claims.Select(c => new ModuleLedgerEntry("io.agentmemory.sdk", c.Key, null, "owned", c.Owner)).ToArray());

    [Fact]
    public void EquivalentShapeUnderAnotherNameCannotMakeIfNotExistsSilentlySkipTheRequestedObject()
    {
        var resolution = Resolve();
        var claims = ModuleMigrationAssessment.Claims(resolution);
        var snapshot = Snapshot(claims, new ModuleDatabaseObject("another_owner_index", "RANGE", "NODE", ["ReviewItem"], ["owner_id"], false, null, "ONLINE"));
        var plan = ModuleMigrationAssessment.Assess(ModuleMigrationCompilation.Compile(resolution, []), snapshot);
        plan.Problems.Should().Contain(p => p.Contains("equivalent", StringComparison.OrdinalIgnoreCase));
    }
    private static ModuleMigrationCompilation Compile(string source)
    {
        var manifest = Resolve().Modules[0];
        var resolution = ModuleResolver.Resolve([manifest with { Ontology = manifest.Ontology! with { Types = [] } }],
            ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        return ModuleMigrationCompilation.Compile(resolution, [new ModuleMigrationStep
        {
            ModuleId = manifest.Id, Key = "0001_update", Statement = source,
            Sha256 = ModuleMigrationDefinitions.Hash(source)
        }]);
    }

    private static ModuleDatabaseSnapshot WithIntent(ModuleMigrationCompilation compilation, string state, long? progress,
        params ModuleDatabaseObject[] objects)
    {
        var snapshot = Snapshot(compilation.Claims, objects);
        var step = compilation.Steps[0].Definition;
        return snapshot with { Entries = snapshot.Entries.Concat([new ModuleLedgerEntry(step.ModuleId, step.Key, step.Sha256, state, null, progress)]).ToArray() };
    }

    private const string Mixed = "CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (n.owner_id, n.due); MATCH (n:ReviewItem) SET n.count = coalesce(n.count, 0) + 1";
    private static ModuleDatabaseObject Due() => new("review_due", "RANGE", "NODE", ["ReviewItem"], ["owner_id", "due"], false, null, "ONLINE");

    [Fact]
    public void HandwrittenStepsAreAssessedWithoutDerivedKeySlicing()
    {
        var compilation = Compile(Mixed);
        var plan = ModuleMigrationAssessment.Assess(compilation, Snapshot(compilation.Claims));
        plan.Problems.Should().BeEmpty();
        plan.Pending.Should().ContainSingle().Which.Key.Should().Be("0001_update");
    }

    [Fact]
    public void MatchingDdlBeforeProgressStampIsRecoverable()
    {
        var compilation = Compile(Mixed);
        ModuleMigrationAssessment.Assess(compilation, WithIntent(compilation, "applying", 0, Due())).Problems.Should().BeEmpty();
    }

    [Fact]
    public void CompletedDdlCannotBeMissingEvenWhileTheWholeMigrationIsApplying()
    {
        var compilation = Compile(Mixed);
        ModuleMigrationAssessment.Assess(compilation, WithIntent(compilation, "applying", 1)).Problems
            .Should().Contain(p => p.Contains("missing"));
    }

    [Fact]
    public void AppliedBackfillNeedsNoInventedSchemaObject()
    {
        var compilation = Compile("MATCH (n:ReviewItem) SET n.note = 'ready'");
        var plan = ModuleMigrationAssessment.Assess(compilation, WithIntent(compilation, "applied", 1L));
        plan.Problems.Should().BeEmpty();
        plan.Applied.Should().ContainSingle();
    }

    [Theory]
    [InlineData("applying", -1L)]
    [InlineData("applying", 3L)]
    [InlineData("applied", 1L)]
    [InlineData("applied", null)]
    [InlineData("applying", null)]
    public void InvalidOrMissingHandwrittenProgressIsRefused(string state, long? progress)
    {
        var compilation = Compile(Mixed);
        ModuleMigrationAssessment.Assess(compilation, WithIntent(compilation, state, progress, Due())).Problems
            .Should().Contain(p => p.Contains("progress"));
    }

    [Fact]
    public void ExactShapeAndReservedHashAreBothChecked()
    {
        var compilation = Compile(Mixed);
        var snapshot = WithIntent(compilation, "applied", 2, Due() with { Properties = ["due", "owner_id"] });
        snapshot = snapshot with { Entries = snapshot.Entries.Select(e => e.Key == "0001_update" ? e with { Hash = "changed" } : e).ToArray() };
        var plan = ModuleMigrationAssessment.Assess(compilation, snapshot);
        plan.Problems.Should().Contain(p => p.Contains("hash"));
        plan.Problems.Should().Contain(p => p.Contains("differs from declared shape"));
    }

    [Fact]
    public void MatchingShapeWithoutAnIntentIsNotAdopted()
    {
        var compilation = Compile(Mixed);
        ModuleMigrationAssessment.Assess(compilation, Snapshot(compilation.Claims, Due())).Problems
            .Should().Contain(p => p.Contains("no matching migration intent"));
    }

    [Fact]
    public void MalformedStoredProgressIsNotCoercedToAnInteger()
    {
        foreach (var value in new object[] { "1", 1.0, true })
        {
            var entry = ModuleLedgerEntry.FromRow(new Dictionary<string, object> { ["completed_statements"] = value });
            entry.InvalidProgress.Should().BeTrue();
        }
    }

}
