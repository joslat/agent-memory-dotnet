using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleMigrationCompilationTests
{
    private static ModuleManifest Module(string id = "reviews", string prefix = "Review") => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example." + id,
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new ModuleOntology { LabelPrefix = prefix, Types = [new() { Name = prefix + "Item" }] }
    };

    private static ModuleResolution Resolve(params ModuleManifest[] modules) =>
        ModuleResolver.Resolve(modules, ModuleVersion.Parse("0.1.0"), new HashSet<string>(StringComparer.Ordinal) { "ModuleSchema" });

    private static ModuleMigrationStep Script(string source, string key = "0001_backfill", string id = "com.example.reviews") =>
        new() { ModuleId = id, Key = key, Statement = source, Sha256 = ModuleMigrationDefinitions.Hash(source) };

    [Fact]
    public void CompilesDependencyThenGeneratedThenOrdinalScriptsAndKeepsWholeSourceHash()
    {
        var dependency = Module("zzz", "Topic");
        var consumer = Module() with { Modules = new Dictionary<string, ModuleVersionRange> { [dependency.Id] = ModuleVersionRange.Parse("[1,2)") } };
        const string source = "// retain this in the hash\r\nCREATE INDEX old_due IF NOT EXISTS FOR (n:ReviewLegacy) ON (n.due);\r\nMATCH (n:ReviewLegacy) SET n.count = coalesce(n.count, 0) + 1;";
        var first = Script(source);
        var later = Script("MATCH (n:ReviewItem) SET n.note = 'ready'", "0002_note");
        var result = ModuleMigrationCompilation.Compile(Resolve(consumer, dependency), [later, first]);
        result.Steps.Take(3).Should().OnlyContain(s => s.Definition.ModuleId == dependency.Id);
        result.Steps.Skip(3).Take(3).Should().OnlyContain(s => s.Definition.Key.StartsWith("derived/"));
        result.Steps.Skip(6).Select(s => s.Definition.Key).Should().Equal(first.Key, later.Key);
        result.Steps[6].Definition.Should().Be(first);
        result.Steps[6].Operations.Should().HaveCount(2);
        result.Steps[6].Operations[0].IsSchema.Should().BeTrue();
        result.Steps[6].Operations[1].IsSchema.Should().BeFalse();
        result.Claims.Should().Contain(c => c.Key == "namespace/label/ReviewLegacy" && c.Owner == consumer.Id);
        result.Claims.Should().Contain(c => c.Key == "namespace/object/old_due" && c.Owner == consumer.Id);
    }

    [Fact]
    public void EmptyCompositionHasNoOperationsOrClaims()
    {
        var result = ModuleMigrationCompilation.Compile(Resolve(), []);
        result.Steps.Should().BeEmpty();
        result.Claims.Should().BeEmpty();
    }

    [Fact]
    public void ChangedSuppliedHashIsRejected()
    {
        var script = Script("MATCH (n:ReviewItem) SET n.note = 'x'") with { Sha256 = new string('0', 64) };
        var action = () => ModuleMigrationCompilation.Compile(Resolve(Module()), [script]);
        action.Should().Throw<FormatException>().WithMessage("*hash*");
    }

    [Theory]
    [InlineData("derived/pretend")]
    [InlineData("1_bad")]
    [InlineData("0001_bad.cypher")]
    [InlineData("0001_bad\n")]
    public void CallerConstructedScriptsCannotBypassResourceKeyRules(string key)
    {
        var action = () => ModuleMigrationCompilation.Compile(Resolve(Module()), [Script("MATCH (n:ReviewItem) SET n.note = 'x'", key)]);
        action.Should().Throw<FormatException>();
    }

    [Fact]
    public void UnknownModuleAndDuplicateScriptKeysAreRejected()
    {
        var script = Script("MATCH (n:ReviewItem) SET n.note = 'x'");
        var unknown = () => ModuleMigrationCompilation.Compile(Resolve(), [script]);
        unknown.Should().Throw<FormatException>();
        var duplicate = () => ModuleMigrationCompilation.Compile(Resolve(Module()), [script, script]);
        duplicate.Should().Throw<FormatException>();
    }

    [Fact]
    public void HistoricalReferenceCannotStealAnotherModulesDeclaredLabel()
    {
        var other = Module("archive", "ReviewArchive");
        var action = () => ModuleMigrationCompilation.Compile(Resolve(Module(), other),
            [Script("MATCH (n:ReviewArchiveItem) SET n.note = 'x'")]);
        action.Should().Throw<FormatException>().WithMessage("*ownership*");
    }

    [Fact]
    public void HandwrittenObjectCannotReuseGeneratedObjectName()
    {
        var action = () => ModuleMigrationCompilation.Compile(Resolve(Module()),
            [Script("CREATE INDEX review_item_id IF NOT EXISTS FOR (n:ReviewItem) ON (n.note)")]);
        action.Should().Throw<FormatException>().WithMessage("*schema object*");
    }

    [Fact]
    public void EquivalentShapeWithAnotherNameIsRejectedInsteadOfSilentlySkippedByNeo4j()
    {
        var action = () => ModuleMigrationCompilation.Compile(Resolve(Module()),
            [Script("CREATE INDEX another_owner IF NOT EXISTS FOR (n:ReviewItem) ON (n.owner_id)")]);
        action.Should().Throw<FormatException>().WithMessage("*equivalent*");
    }

    [Fact]
    public void EmptyOrExcessivelyManyScriptsAreRejected()
    {
        var empty = () => ModuleMigrationCompilation.Compile(Resolve(Module()), [Script("// nothing")]);
        empty.Should().Throw<FormatException>();
        var scripts = Enumerable.Range(0, 129).Select(i => Script("MATCH (n:ReviewItem) SET n.note = 'x'", $"{i:0000}_note")).ToArray();
        var excess = () => ModuleMigrationCompilation.Compile(Resolve(Module()), scripts);
        excess.Should().Throw<FormatException>();
    }
}
