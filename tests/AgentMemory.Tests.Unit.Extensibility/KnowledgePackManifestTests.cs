using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Knowledge pack manifests and search views (Extensibility 0.13 §K1, §K4): editor and runtime agree.</summary>
public sealed class KnowledgePackManifestTests
{
    internal const string Pack = """
        {"format":"agentmemory.module/v1alpha1","id":"io.example.dojo","version":"1.0.0","sdk":"[0.1,0.2)",
         "seed":["seed/records.jsonl"],
         "ontology":{"labelPrefix":"Dojo","types":[
           {"name":"DojoKata","scope":"shared","render":"{name}: {summary}","properties":{
             "name":{"type":"string","required":true,"maxLength":100},
             "summary":{"type":"string","maxLength":400},
             "moves":{"type":"int","min":1}}},
           {"name":"DojoRank","scope":"shared","render":"{name}","properties":{"name":{"type":"string","required":true}}}],
           "relations":[{"name":"DOJO_KATA_FOR_RANK","from":"DojoKata","to":"DojoRank","max":1}]},
         "views":[{"id":"kata","type":"DojoKata","search":{"properties":["name","summary"]},"limit":12}]}
        """;

    // The schema library registers each schema id once per process, so the one cached loader is shared.
    private static bool EditorAccepts(string json) => ModuleEditorSchemaTests.EditorAccepts("module", json);

    private static IReadOnlyList<string> RuntimeProblems(string json)
    {
        try { return ModuleManifest.Parse(json).Validate(); }
        catch (FormatException e) { return [e.Message]; }
    }

    [Fact]
    public void AKnowledgePackManifestIsAcceptedByTheEditorAndTheRuntime()
    {
        EditorAccepts(Pack).Should().BeTrue();
        RuntimeProblems(Pack).Should().BeEmpty();
        var manifest = ModuleManifest.Parse(Pack);
        manifest.IsKnowledgePack.Should().BeTrue();
        manifest.Seed.Should().Equal("seed/records.jsonl");
        manifest.Views.Single().Search!.Properties.Should().Equal("name", "summary");
    }

    [Theory]
    [InlineData("seed-escape")]
    [InlineData("seed-not-jsonl")]
    [InlineData("seed-empty")]
    [InlineData("search-order")]
    [InlineData("search-history")]
    [InlineData("search-limit")]
    [InlineData("search-no-properties")]
    [InlineData("search-unknown-field")]
    public void EditorAndRuntimeRejectTheSameShapeMistakes(string mutation)
    {
        var json = Mutate(mutation);
        EditorAccepts(json).Should().BeFalse(mutation);
        RuntimeProblems(json).Should().NotBeEmpty(mutation);
    }

    [Theory]
    [InlineData("owner-type", "knowledge pack types are shared")]
    [InlineData("host-feature", "requires no host features")]
    [InlineData("dependency", "depends on no module")]
    [InlineData("search-undeclared", "declared string properties")]
    [InlineData("search-not-string", "declared string properties")]
    public void TheRuntimeChecksWhatTheEditorCannot(string mutation, string problem)
    {
        RuntimeProblems(Mutate(mutation)).Should().Contain(p => p.Contains(problem, StringComparison.Ordinal), mutation);
    }

    [Fact]
    public void AModuleWithoutSeedOrSearchKeepsItsCanonicalJsonAndSoItsFingerprint()
    {
        var root = JsonNode.Parse(Pack)!.AsObject();
        root.Remove("seed");
        root["views"]![0]!.AsObject().Remove("search");
        var plain = ModuleManifest.Parse(root.ToJsonString());
        plain.IsKnowledgePack.Should().BeFalse();
        var canonical = JsonSerializer.Serialize(plain);
        canonical.Should().NotContain("Seed").And.NotContain("Search").And.NotContain("IsKnowledgePack");
    }

    [Fact]
    public void ASearchViewDerivesOneFullTextIndexOverItsProperties()
    {
        var steps = ModuleSchemaDerivation.Derive(ModuleManifest.Parse(Pack));
        var search = steps.Single(s => s.Key == "derived/dojo_kata_kata_search");
        search.Statement.Should().Be("CREATE FULLTEXT INDEX `dojo_kata_kata_search` IF NOT EXISTS FOR (n:`DojoKata`) ON EACH [n.`name`, n.`summary`];");
        var shape = ModuleSchemaObjects.Describe(ModuleManifest.Parse(Pack)).Single(s => s.Name == "dojo_kata_kata_search");
        shape.Kind.Should().Be(ModuleSchemaObjectKind.FullTextIndex);
        shape.Properties.Should().Equal("name", "summary");
    }

    [Fact]
    public void TheMigratorParsesItsOwnFullTextStatementIntoTheSameShape()
    {
        var parsed = ModuleSchemaStatement.Parse("CREATE FULLTEXT INDEX `dojo_kata_kata_search` IF NOT EXISTS FOR (n:`DojoKata`) ON EACH [n.`name`, n.`summary`];");
        parsed.Kind.Should().Be("FULLTEXT");
        parsed.IsConstraint.Should().BeFalse();
        parsed.Labels.Should().Equal("DojoKata");
        parsed.Properties.Should().Equal("name", "summary");
    }

    [Fact]
    public void TwoModulesCannotClaimTheSameSearchIndexName()
    {
        var other = JsonNode.Parse(Pack)!;
        other["id"] = "io.example.other";
        var resolution = ModuleResolver.Resolve([ModuleManifest.Parse(Pack), ModuleManifest.Parse(other.ToJsonString())], ModuleVersion.Parse("0.1.0"),
            ModuleHostFeatures.Known);
        resolution.Succeeded.Should().BeFalse();
        resolution.Errors.Should().Contain(e => e.Contains("dojo_kata_kata_search", StringComparison.Ordinal));
    }

    private static string Mutate(string mutation)
    {
        var root = JsonNode.Parse(Pack)!;
        var view = root["views"]![0]!;
        var kata = root["ontology"]!["types"]![0]!;
        switch (mutation)
        {
            case "seed-escape": root["seed"] = new JsonArray("../records.jsonl"); break;
            case "seed-not-jsonl": root["seed"] = new JsonArray("seed/records.json"); break;
            case "seed-empty": root["seed"] = new JsonArray(); break;
            case "search-order": view["orderBy"] = "name"; break;
            case "search-history": view["selection"] = "history"; break;
            case "search-limit": view["limit"] = 30; break;
            case "search-no-properties": view["search"]!["properties"] = new JsonArray(); break;
            case "search-unknown-field": view["search"]!["fuzzy"] = true; break;
            case "owner-type": kata["scope"] = "owner"; break;
            case "host-feature": root["requires"] = new JsonObject { ["host"] = new JsonArray("Inference") }; break;
            case "dependency": root["requires"] = new JsonObject { ["modules"] = new JsonObject { ["io.example.other"] = "[1,2)" } }; break;
            case "search-undeclared": view["search"]!["properties"] = new JsonArray("title"); break;
            case "search-not-string": view["search"]!["properties"] = new JsonArray("moves"); break;
        }
        return root.ToJsonString();
    }
}
