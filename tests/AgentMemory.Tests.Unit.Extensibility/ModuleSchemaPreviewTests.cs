using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleSchemaPreviewTests
{
    private static ModuleManifest Manifest() => ModuleManifest.Parse("""
        {"format":"agentmemory.module/v1alpha1","id":"com.example.preview","version":"0.1.0","sdk":"[0.1,0.2)",
         "requires":{"modules":{"com.example.topics":"[0.1,0.2)"}},
         "ontology":{"labelPrefix":"Preview","types":[{"name":"PreviewItem","properties":{"dueAt":{"type":"datetime","indexed":true}}}]}}
        """);

    [Fact]
    public void LocalEditorPreviewDoesNotRequireActivatingOrInventingDependencies()
    {
        var steps = ModuleSchemaDerivation.Derive(Manifest());
        steps.Should().HaveCount(4);
        steps[0].Key.Should().Be("derived/preview_item_due_at_idx");
        steps[0].Statement.Should().Be("CREATE INDEX `preview_item_due_at_idx` IF NOT EXISTS FOR (n:`PreviewItem`) ON (n.`dueAt`);");
        steps[1].Statement.Should().Be("CREATE CONSTRAINT `preview_item_id` IF NOT EXISTS FOR (n:`PreviewItem`) REQUIRE n.`id` IS UNIQUE;");
        steps.Should().OnlyContain(s => s.ModuleId == "com.example.preview");
        steps.Should().OnlyContain(s => s.Sha256 == ModuleMigrationDefinitions.Hash(s.Statement));
        ((ICollection<ModuleMigrationStep>)steps).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void PreviewUsesExactlyTheDefinitionsAppliedByTheMigrator()
    {
        var manifest = Manifest() with { Modules = new Dictionary<string, ModuleVersionRange>() };
        var resolved = ModuleResolver.Resolve([manifest], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        ModuleSchemaDerivation.Derive(manifest).Should().Equal(ModuleMigrationDefinitions.Derive(resolved));
    }

    [Fact]
    public void InvalidLocalMetadataCannotProduceDDL()
    {
        FluentActions.Invoking(() => ModuleSchemaDerivation.Derive(Manifest() with { Id = "invalid" }))
            .Should().Throw<FormatException>();
        FluentActions.Invoking(() => ModuleSchemaDerivation.Derive(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CodeOnlyModuleHasNoSchemaPreview()
    {
        ModuleSchemaDerivation.Derive(Manifest() with { Ontology = null }).Should().BeEmpty();
    }
}
