using System.Collections;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleMigrationDefinitionTests
{
    private static ModuleManifest Module(string id, string prefix, params string[] labels) => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example." + id,
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new ModuleOntology
        {
            LabelPrefix = prefix,
            Types = labels.Select(label => new ModuleType
            {
                Name = label,
                Properties = new Dictionary<string, ModuleProperty>
                {
                    ["dueAt"] = new() { Type = "datetime", Indexed = true },
                    ["description"] = new() { Type = "string" }
                }
            }).ToArray()
        }
    };

    private static ModuleResolution Resolve(params ModuleManifest[] modules) =>
        ModuleResolver.Resolve(modules, ModuleVersion.Parse("0.1.0"), new HashSet<string>(StringComparer.Ordinal) { "ModuleSchema" });

    [Fact]
    public void EmptyCompositionHasNoDerivedWork() =>
        ModuleMigrationDefinitions.Derive(Resolve()).Should().BeEmpty();

    [Fact]
    public void DerivedDdlPreservesExactPropertyIdentityAndUsesStableKeys()
    {
        var steps = ModuleMigrationDefinitions.Derive(Resolve(Module("reviews", "Review", "ReviewItem")));
        steps.Should().HaveCount(4);
        steps.Select(step => step.Key).Should().Equal(
            "derived/review_item_due_at_idx", "derived/review_item_id",
            "derived/review_item_key_id_idx", "derived/review_item_owner_id_idx");
        steps[0].Statement.Should().Be("CREATE INDEX `review_item_due_at_idx` IF NOT EXISTS FOR (n:`ReviewItem`) ON (n.`dueAt`);");
        steps[1].Statement.Should().Be("CREATE CONSTRAINT `review_item_id` IF NOT EXISTS FOR (n:`ReviewItem`) REQUIRE n.`id` IS UNIQUE;");
        steps.Should().OnlyContain(step => step.ModuleId == "com.example.reviews");
        steps.Should().OnlyContain(step => step.Sha256 == ModuleMigrationDefinitions.Hash(step.Statement));
        steps.Should().NotContain(step => step.Statement.Contains("description", StringComparison.Ordinal));
    }

    [Fact]
    public void DependencyOrderPrecedesAlphabeticalModuleOrder()
    {
        var dependency = Module("zzz", "Topic", "TopicItem");
        var consumer = Module("aaa", "Review", "ReviewItem") with
        { Modules = new Dictionary<string, ModuleVersionRange> { [dependency.Id] = ModuleVersionRange.Parse("[1,2)") } };
        var steps = ModuleMigrationDefinitions.Derive(Resolve(consumer, dependency));
        steps.Take(4).Should().OnlyContain(step => step.ModuleId == dependency.Id);
        steps.Skip(4).Should().OnlyContain(step => step.ModuleId == consumer.Id);
    }

    [Fact]
    public void AddingATypeKeepsExistingKeysStatementsAndHashes()
    {
        var before = ModuleMigrationDefinitions.Derive(Resolve(Module("reviews", "Review", "ReviewItem")));
        var after = ModuleMigrationDefinitions.Derive(Resolve(Module("reviews", "Review", "ReviewArchive", "ReviewItem")));
        after.Should().HaveCount(8);
        after.Where(step => before.Any(old => old.Key == step.Key)).Should().Equal(before);
    }

    [Fact]
    public void FailedWholeSetResolutionCannotProducePartialMigrations()
    {
        var invalid = Module("reviews", "Review", "ReviewItem") with { Id = "invalid" };
        var result = Resolve(Module("valid", "Topic", "TopicItem"), invalid);
        result.Succeeded.Should().BeFalse();
        var action = () => ModuleMigrationDefinitions.Derive(result);
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a\nb\n")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\rb\r")]
    public void MigrationHashNormalizesOnlyLineEndings(string statement)
    {
        ModuleMigrationDefinitions.Hash(statement).Should().Be("911169ddaaf146aff539f58c26c489af3b892dff0fe283c1c264c65ae5aa59a2");
        ModuleMigrationDefinitions.Hash(statement).Should().NotBe(ModuleMigrationDefinitions.Hash("a\nb"));
    }

    [Fact]
    public void InputOrderDoesNotChangeDerivedStatementsOrHashes()
    {
        var a = Module("reviews", "Review", "ReviewItem", "ReviewArchive");
        var b = Module("topics", "Topic", "TopicItem");
        ModuleMigrationDefinitions.Derive(Resolve(a, b))
            .Should().Equal(ModuleMigrationDefinitions.Derive(Resolve(b, a)));
    }

    [Fact]
    public void ReturnedDefinitionsAreReadOnly()
    {
        var steps = ModuleMigrationDefinitions.Derive(Resolve(Module("reviews", "Review", "ReviewItem")));
        ((IList)steps).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void ValidQuotedKeywordPropertyIsNotTreatedAsDestructiveCypher()
    {
        var module = Module("reviews", "Review", "ReviewItem") with
        {
            Ontology = new ModuleOntology
            {
                LabelPrefix = "Review",
                Types = [new ModuleType
                {
                    Name = "ReviewItem",
                    Properties = new Dictionary<string, ModuleProperty>
                    { ["delete"] = new() { Type = "bool", Indexed = true } }
                }]
            }
        };
        var resolution = Resolve(module);
        resolution.Succeeded.Should().BeTrue();
        ModuleMigrationDefinitions.Derive(resolution).Should().Contain(step =>
            step.Statement == "CREATE INDEX `review_item_delete_idx` IF NOT EXISTS FOR (n:`ReviewItem`) ON (n.`delete`);");
    }
}
