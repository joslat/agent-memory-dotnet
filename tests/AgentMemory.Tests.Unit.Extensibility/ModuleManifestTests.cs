using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleManifestTests
{
    private const string Minimal = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.reviews","version":"0.1.0","sdk":"[0.1,0.2)"}
        """;
    private const string Example = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.reviews","version":"0.1.0","sdk":"[0.1,0.2)",
         "requires":{"modules":{"com.example.topics":"[0.1,0.2)"},"host":["History"]},
         "ontology":{"labelPrefix":"Review","types":[{"name":"ReviewItem","scope":"shared",
           "properties":{"prompt":{"type":"string","required":true,"maxLength":400},
             "next_review":{"type":"datetime","indexed":true,"description":"When to review"},
             "count":{"type":"int","min":0,"max":9223372036854775807},
             "state":{"type":"string","values":["new","known"]}},
           "render":"{prompt} (due {next_review:d})"}],
           "relations":[{"name":"REVIEW_IN_TOPIC","from":"ReviewItem","to":"com.example.topics:Topic","max":1,
             "description":"Topic grouping"}]},
         "views":[{"id":"due","type":"ReviewItem","where":[["next_review","<=","$now"]],
           "orderBy":{"property":"next_review","descending":true},"limit":5}]}
        """;

    [Fact]
    public void ParsesCodeOnlyModuleWithoutInventingOntologyOrRequirements()
    {
        var m = ModuleManifest.Parse(Minimal);
        m.Id.Should().Be("com.example.reviews");
        m.Version.Value.Should().Be("0.1.0");
        m.Sdk.Contains(ModuleVersion.Parse("0.1.1")).Should().BeTrue();
        m.Modules.Should().BeEmpty(); m.Host.Should().BeEmpty(); m.Views.Should().BeEmpty();
        m.Ontology.Should().BeNull();
    }

    [Fact]
    public void RetainsTypedOntologyAndLosslessLiteralsAfterTheParserDocumentIsDisposed()
    {
        var m = ModuleManifest.Parse(Example);
        m.Modules["com.example.topics"].Value.Should().Be("[0.1,0.2)");
        m.Host.Should().Equal("History");
        var t = m.Ontology!.Types.Single();
        t.Scope.Should().Be("shared");
        t.Properties["count"].Maximum!.Value.GetInt64().Should().Be(long.MaxValue);
        t.Properties["prompt"].Required.Should().BeTrue();
        t.Properties["next_review"].Description.Should().Be("When to review");
        m.Ontology.Relations.Single().Description.Should().Be("Topic grouping");
        var view = m.Views.Single();
        view.OrderBy!.Descending.Should().BeTrue();
        view.Where.Single().Value.GetString().Should().Be("$now");
    }

    [Fact]
    public void StringSortIsAscendingAndCollectionsCannotBeMutatedThroughTheirRuntimeTypes()
    {
        var m = ModuleManifest.Parse(Example.Replace("{\"property\":\"next_review\",\"descending\":true}", "\"next_review\""));
        m.Views.Single().OrderBy!.Descending.Should().BeFalse();
        ((IList<ModuleView>)m.Views).IsReadOnly.Should().BeTrue();
        ((IDictionary<string, ModuleVersionRange>)m.Modules).IsReadOnly.Should().BeTrue();
        ((IDictionary<string, ModuleProperty>)m.Ontology!.Types[0].Properties).IsReadOnly.Should().BeTrue();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"format\":42}")]
    public void RejectsMissingOrWrongRootShape(string json) =>
        FluentActions.Invoking(() => ModuleManifest.Parse(json)).Should().Throw<FormatException>();

    [Theory]
    [InlineData("\"version\":\"0.1.0\"", "\"version\":\"01.0.0\"")]
    [InlineData("\"sdk\":\"[0.1,0.2)\"", "\"sdk\":\"*\"")]
    [InlineData("com.example.reviews", "reviews")]
    [InlineData("agentmemory.module/v1alpha1", "agentmemory.module/v99")]
    [InlineData("\"version\":\"0.1.0\"", "\"version\":\"0.1.0\",\"Version\":\"2.0.0\"")]
    [InlineData("\"version\":\"0.1.0\"", "\"version\":\"0.1.0\",\"version\":\"2.0.0\"")]
    public void RejectsInvalidIdentityVersionAndAmbiguousOrUnknownFields(string before, string after) =>
        FluentActions.Invoking(() => ModuleManifest.Parse(Minimal.Replace(before, after))).Should().Throw<FormatException>();

    [Theory]
    [InlineData("\"host\":[\"History\"]", "\"host\":[\"Hstory\"]")]
    [InlineData("\"host\":[\"History\"]", "\"host\":[\"History\",\"History\"]")]
    [InlineData("\"required\":true", "\"required\":\"true\"")]
    [InlineData("\"maxLength\":400", "\"maxLength\":1.5")]
    [InlineData("\"maxLength\":400", "\"maxLength\":null")]
    [InlineData("\"maxLength\":400", "\"maxlength\":400")]
    [InlineData("\"required\":true", "\"required\":true,\"required\":false")]
    [InlineData("\"scope\":\"shared\"", "\"scope\":\"global\"")]
    [InlineData("\"type\":\"int\"", "\"type\":\"integer\"")]
    [InlineData("\"<=\"", "\"MATCH\"")]
    [InlineData("\"$now\"", "{\"query\":\"anything\"}")]
    [InlineData("\"limit\":5", "\"limit\":0")]
    public void RejectsMalformedNestedDeclarationsWithAPath(string before, string after) =>
        FluentActions.Invoking(() => ModuleManifest.Parse(Example.Replace(before, after)))
            .Should().Throw<FormatException>().WithMessage("*$*");

    [Fact]
    public void RejectsRetiredRoutingWithMigrationGuidance()
    {
        var json = Minimal[..^1] + ",\"routing\":{\"mandatory\":true}}";
        FluentActions.Invoking(() => ModuleManifest.Parse(json)).Should().Throw<FormatException>()
            .WithMessage("*routing*applicable contributors*");
    }

    [Fact]
    public void BoundedUtf8AndDepthRefuseOversizeInputBeforeModelOrAssemblyWork()
    {
        var tooLarge = Minimal.Insert(1, "\"padding\":\"" + new string('\u20ac', ModuleManifest.MaximumUtf8Bytes / 2) + "\",");
        FluentActions.Invoking(() => ModuleManifest.Parse(tooLarge)).Should().Throw<FormatException>().WithMessage("*UTF-8*");
        var deep = new string('[', 40) + "0" + new string(']', 40);
        FluentActions.Invoking(() => ModuleManifest.Parse(deep)).Should().Throw<FormatException>();
    }

    [Fact]
    public void RejectsCommentsTrailingCommasAndDuplicateEscapedNames()
    {
        foreach (var json in new[] { Minimal.Insert(1,"/* comment */"), Minimal.Insert(Minimal.Length-1,","),
            Minimal.Replace("\"version\":\"0.1.0\"", "\"version\":\"0.1.0\",\"vers\\u0069on\":\"0.1.0\"") })
            FluentActions.Invoking(() => ModuleManifest.Parse(json)).Should().Throw<FormatException>();
    }
}
