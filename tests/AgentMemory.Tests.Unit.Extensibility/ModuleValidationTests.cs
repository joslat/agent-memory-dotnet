using System.Reflection;
using System.Text.Json;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleValidationTests
{
    private const string Valid = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.reviews","version":"0.1.0","sdk":"[0.1,0.2)",
         "requires":{"modules":{"com.example.topics":"[0.1,0.2)"}},
         "ontology":{"labelPrefix":"Review","types":[{"name":"ReviewItem","scope":"shared",
           "properties":{"prompt":{"type":"string","required":true,"maxLength":400},
             "next_review":{"type":"datetime"},"count":{"type":"int","min":0,"max":9223372036854775807},
             "active":{"type":"bool"},"day":{"type":"date"},
             "state":{"type":"string","values":["new","known"]}},
           "render":"{{Review}} {prompt} (due {next_review:d})"}],
           "relations":[{"name":"REVIEW_ABOUT","from":"ReviewItem","to":"core:Entity","required":true},
             {"name":"REVIEW_IN_TOPIC","from":"ReviewItem","to":"com.example.topics:Topic","max":1}]},
         "views":[{"id":"due","type":"ReviewItem","where":[["next_review","<=","$now"]],
           "orderBy":{"property":"next_review","descending":true},"limit":5}]}
        """;

    [Fact]
    public void AcceptsOwnedTypesDeclaredDependencyLinksEscapesAndTypedViews() =>
        ModuleManifest.Parse(Valid).Validate().Should().BeEmpty();

    [Theory]
    [InlineData("\"labelPrefix\":\"Review\"", "\"labelPrefix\":\"review\"", "prefix")]
    [InlineData("\"name\":\"ReviewItem\"", "\"name\":\"OtherItem\"", "prefix")]
    [InlineData("\"prompt\":{", "\"owner_id\":{", "reserved")]
    [InlineData("\"prompt\":{", "\"source_excerpt\":{", "reserved")]
    [InlineData("\"prompt\":{", "\"bad`name\":{", "identifier")]
    [InlineData("{prompt}", "{missing}", "render")]
    [InlineData("{prompt}", "{prompt", "render")]
    [InlineData("REVIEW_ABOUT", "OTHER_ABOUT", "relationship")]
    [InlineData("\"from\":\"ReviewItem\"", "\"from\":\"core:Entity\"", "source")]
    [InlineData("core:Entity", "core:Message", "target")]
    [InlineData("com.example.topics:Topic", "com.other.topics:Topic", "dependency")]
    [InlineData("com.example.topics:Topic", "MissingLocalType", "target")]
    [InlineData("\"property\":\"next_review\"", "\"property\":\"created_at\"", "orderBy")]
    public void ReportsSemanticErrorsInsteadOfTreatingParsedDataAsActivationPermission(string before, string after, string expected) =>
        ModuleManifest.Parse(Valid.Replace(before, after)).Validate().Should().Contain(e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("\"type\":\"int\",\"min\":0", "\"type\":\"bool\",\"min\":0")]
    [InlineData("\"min\":0", "\"min\":1.5")]
    [InlineData("\"max\":9223372036854775807", "\"max\":9223372036854775808")]
    [InlineData("\"min\":0,\"max\":9223372036854775807", "\"min\":9223372036854775807,\"max\":9223372036854775806")]
    [InlineData("\"type\":\"string\",\"values\"", "\"type\":\"bool\",\"values\"")]
    [InlineData("\"type\":\"string\",\"required\":true", "\"type\":\"int\",\"required\":true")]
    public void EnforcesPropertyConstraintsWithoutRoundingLongIntegers(string before, string after) =>
        ModuleManifest.Parse(Valid.Replace(before, after)).Validate().Should().NotBeEmpty();

    [Theory]
    [InlineData("count", "=", "42", true)]
    [InlineData("count", "in", "[0,9223372036854775807]", true)]
    [InlineData("count", "=", "1.5", false)]
    [InlineData("count", "=", "9223372036854775808", false)]
    [InlineData("count", "in", "[0,\"1\"]", false)]
    [InlineData("count", "in", "42", false)]
    [InlineData("active", "=", "true", true)]
    [InlineData("active", "<", "true", false)]
    [InlineData("prompt", "=", "\"$session\"", true)]
    [InlineData("prompt", "=", "\"$now\"", false)]
    [InlineData("count", "=", "\"$session\"", false)]
    [InlineData("day", "=", "\"2024-02-29\"", true)]
    [InlineData("day", "=", "\"2025-02-29\"", false)]
    [InlineData("next_review", "<=", "\"2026-10-14T00:00:00Z\"", true)]
    [InlineData("next_review", "<=", "\"2026-10-14T00:00:00\"", false)]
    [InlineData("next_review", "=", "null", true)]
    [InlineData("next_review", "<", "null", false)]
    [InlineData("missing", "=", "42", false)]
    public void ChecksFilterLiteralAndPlaceholderTypes(string property, string op, string json, bool valid)
    {
        var m = ModuleManifest.Parse(Valid);
        var filter = new ModuleViewFilter { Property = property, Operator = op, Value = JsonDocument.Parse(json).RootElement.Clone() };
        var errors = (m with { Views = [m.Views[0] with { Where = [filter] }] }).Validate();
        if (valid) errors.Should().BeEmpty();
        else errors.Should().NotBeEmpty();
    }

    [Fact]
    public void ChecksManuallyConstructedManifestsAndReturnsAllErrorsInStableOrder()
    {
        var m = ModuleManifest.Parse(Valid);
        var bad = m with { Id = "bad", Host = ["Unknown"], Views = [m.Views[0] with { Limit = 0, Id = "foreign:id" }],
            Ontology = m.Ontology! with { Types = [m.Ontology.Types[0], m.Ontology.Types[0]] } };
        var errors = bad.Validate();
        errors.Count.Should().BeGreaterThanOrEqualTo(5);
        errors.Should().BeInAscendingOrder(StringComparer.Ordinal);
        ((ICollection<string>)errors).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void NullCollectionsInConsumerBuiltDataReturnProblemsRatherThanNullReferenceErrors()
    {
        var m = ModuleManifest.Parse(Valid) with { Modules = null!, Host = null!, Views = null!,
            Ontology = new ModuleOntology { LabelPrefix = "Review", Types = null!, Relations = null! } };
        m.Validate().Count.Should().BeGreaterThanOrEqualTo(5);
    }

    [Theory]
    [InlineData("Fact")]
    [InlineData("User")]
    [InlineData("Migration")]
    [InlineData("ModuleMigration")]
    public void RefusesCoreAndBookkeepingLabels(string name)
    {
        var m = ModuleManifest.Parse(Valid);
        (m with { Ontology = new ModuleOntology { LabelPrefix = name, Types = [new ModuleType { Name = name }] }, Views = [] })
            .Validate().Should().Contain(e => e.Contains("reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsNamesThatWouldGenerateTheSameSchemaIdentifiers()
    {
        var m = ModuleManifest.Parse(Valid);
        (m with { Ontology = m.Ontology! with { Types = [new ModuleType { Name = "ReviewURL" }, new ModuleType { Name = "ReviewUrl" }] }, Views = [] })
            .Validate().Should().Contain(e => e.Contains("normalization", StringComparison.Ordinal));
        (m with { Views = [m.Views[0], m.Views[0]] }).Validate().Should().Contain(e => e.Contains("duplicate view", StringComparison.Ordinal));
    }

    [Fact]
    public void CallerDictionaryComparerCannotMakeAPropertyReferenceCaseInsensitive()
    {
        var m = ModuleManifest.Parse(Valid);
        var type = m.Ontology!.Types[0] with { Properties = new Dictionary<string, ModuleProperty>(StringComparer.OrdinalIgnoreCase)
            { ["prompt"] = new ModuleProperty { Type = "string" } }, Render = null };
        var view = m.Views[0] with { Where = [], OrderBy = new ModuleViewOrder { Property = "PROMPT" } };
        (m with { Ontology = m.Ontology with { Types = [type] }, Views = [view] }).Validate()
            .Should().Contain(e => e.Contains("orderBy", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposedJsonAndNonFiniteNumbersCannotReachAViewOrNumericBound()
    {
        var m = ModuleManifest.Parse(Valid);
        var doc = JsonDocument.Parse("42");var value = doc.RootElement;doc.Dispose();
        (m with { Views = [m.Views[0] with { Where = [new ModuleViewFilter { Property = "count", Operator = "=", Value = value }] }] })
            .Validate().Should().Contain(e => e.Contains("disposed", StringComparison.Ordinal));
        var type = m.Ontology!.Types[0] with { Properties = new Dictionary<string, ModuleProperty>
            { ["count"] = new ModuleProperty { Type = "float", Maximum = JsonDocument.Parse("1e999").RootElement.Clone() } }, Render = null };
        (m with { Ontology = m.Ontology with { Types = [type] }, Views = [] }).Validate()
            .Should().Contain(e => e.Contains("numeric type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CallerConstructedTypeTextCannotBypassTheReadersNonemptyRule(bool description)
    {
        var m = ModuleManifest.Parse(Valid);
        var type = description ? m.Ontology!.Types[0] with { Description = " " }
            : m.Ontology!.Types[0] with { Color = " " };
        (m with { Ontology = m.Ontology with { Types = [type] } }).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void ReservedVocabularyCoversActualBaseConstantsAndAllBuiltInExtensions()
    {
        var abstraction = Assembly.Load("AgentMemory.Abstractions");
        foreach (var (nested, reserved) in new[] { ("NodeLabels", ModuleReservedNames.Labels), ("RelationshipTypes", ModuleReservedNames.Relationships) })
        {
            var type = abstraction.GetType("AgentMemory.Abstractions.Schema.SchemaConstants+" + nested)!;
            var actual = type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!);
            reserved.Should().Contain(actual);
        }
        var neo = Assembly.Load("AgentMemory.Neo4j");
        foreach (var type in neo.GetTypes().Where(t => !t.IsAbstract && t.GetInterfaces().Any(i => i.Name == "ISchemaExtension")))
        {
            var extension = Activator.CreateInstance(type, nonPublic: true)!;
            ((IEnumerable<string>)type.GetProperty("DeclaredLabels")!.GetValue(extension)!).Except(ModuleReservedNames.Labels).Should().BeEmpty();
            ((IEnumerable<string>)type.GetProperty("DeclaredRelationshipTypes")!.GetValue(extension)!).Except(ModuleReservedNames.Relationships).Should().BeEmpty();
        }
    }
}
