using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Json.Schema;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleEditorSchemaTests
{
    private const string Manifest = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.reviews","version":"1.0.0-beta.1+build.2","sdk":"[0.1,0.2)",
         "requires":{"modules":{"com.example.topics":"[1,2)"},"host":["History"]},
         "ontology":{"labelPrefix":"Review","types":[{"name":"ReviewItem","properties":{
           "state":{"type":"string","values":["new","known"],"description":"Status"},
           "count":{"type":"int","min":0,"max":9223372036854775807},"due":{"type":"datetime","indexed":true}},
           "render":"{state} {due:d}"}],"relations":[{"name":"REVIEW_TOPIC","from":"ReviewItem","to":"com.example.topics:TopicItem"}]},
         "views":[{"id":"due","type":"ReviewItem","where":[["due","<=","$now"]],"orderBy":{"property":"due","descending":true},"limit":5}]}
        """;
    private const string Profile = """
        {"format":"agentmemory.modules/v1alpha1","stores":{"memory":{"provider":"neo4j-direct"}},
         "modules":[{"id":"com.example.reviews","version":"[1,2)","store":"memory","manifest":"review.json"}]}
        """;
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> Schemas = new(StringComparer.Ordinal);

    private static bool EditorAccepts(string kind, string json)
    {
        var file = Path.Combine(AppContext.BaseDirectory,"ModuleSchemas", $"agentmemory.{kind}.v1alpha1.schema.json");
        var schema = Schemas.GetOrAdd(kind, _ => new Lazy<JsonSchema>(() => JsonSchema.FromText(File.ReadAllText(file)))).Value;
        using var document = JsonDocument.Parse(json);
        return schema.Evaluate(document.RootElement).IsValid;
    }
    private static bool RuntimeAccepts(string kind, string json)
    {
        try
        {
            if (kind == "module") return ModuleManifest.Parse(json).Validate().Count == 0;
            _ = ModuleCompositionProfile.Parse(json); return true;
        }
        catch (FormatException) { return false; }
    }

    [Theory]
    [InlineData("module")]
    [InlineData("modules")]
    public void EditorAndRuntimeAcceptRepresentativeDeclarations(string kind)
    {
        var json = kind == "module" ? Manifest : Profile;
        EditorAccepts(kind,json).Should().BeTrue(); RuntimeAccepts(kind,json).Should().BeTrue();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("feature")]
    [InlineData("property-type")]
    [InlineData("numeric-enum")]
    [InlineData("max-length")]
    [InlineData("missing-name")]
    [InlineData("empty-values")]
    [InlineData("duplicate-values")]
    [InlineData("integer-overflow")]
    [InlineData("view-limit")]
    [InlineData("view-operator")]
    [InlineData("description-length")]
    [InlineData("version")]
    public void EditorAndRuntimeRejectInvalidManifestFields(string mutation)
    {
        var root = JsonNode.Parse(Manifest)!;
        var type = root["ontology"]!["types"]![0]!; var props = type["properties"]!;
        switch (mutation)
        {
            case "unknown": root["routing"] = true; break;
            case "feature": root["requires"]!["host"]![0] = "Remindres"; break;
            case "property-type": props["state"]!["type"] = "object"; break;
            case "numeric-enum": props["state"]!["type"] = "int"; break;
            case "max-length": props["due"]!["maxLength"] = 20; break;
            case "missing-name": type.AsObject().Remove("name"); break;
            case "empty-values": props["state"]!["values"] = new JsonArray(); break;
            case "duplicate-values": props["state"]!["values"] = new JsonArray("new","new"); break;
            case "integer-overflow": props["count"]!["max"] = JsonNode.Parse("9223372036854775808"); break;
            case "view-limit": root["views"]![0]!["limit"] = 0; break;
            case "view-operator": root["views"]![0]!["where"]![0]![1] = "matches"; break;
            case "description-length": props["state"]!["description"] = new string('a',201); break;
            case "version": root["version"] = "01.0.0"; break;
        }
        var json = root.ToJsonString();
        EditorAccepts("module",json).Should().BeFalse(mutation); RuntimeAccepts("module",json).Should().BeFalse(mutation);
    }

    [Theory]
    [InlineData("two-sources")]
    [InlineData("no-source")]
    [InlineData("pin")]
    [InlineData("configuration")]
    [InlineData("unknown")]
    [InlineData("store")]
    public void EditorAndRuntimeRejectInvalidProfileFields(string mutation)
    {
        var root = JsonNode.Parse(Profile)!;var entry = root["modules"]![0]!;
        switch (mutation)
        {
            case "two-sources": entry["assembly"] = "review.dll"; break;
            case "no-source": entry.AsObject().Remove("manifest"); break;
            case "pin": entry["sha256"] = "not-a-digest"; break;
            case "configuration": entry["configuration"] = "AgentMemory::Reviews"; break;
            case "unknown": entry["automatic"] = true; break;
            case "store": entry["store"] = "bad/store"; break;
        }
        var json = root.ToJsonString();
        EditorAccepts("modules",json).Should().BeFalse(mutation); RuntimeAccepts("modules",json).Should().BeFalse(mutation);
    }

    [Fact]
    public void SchemaDoesNotReplaceRuntimeCrossFieldValidation()
    {
        var json = Manifest.Replace("[0.1,0.2)","[9,1)",StringComparison.Ordinal);
        EditorAccepts("module",json).Should().BeTrue(); RuntimeAccepts("module",json).Should().BeFalse();
        json = Manifest.Replace("{state} {due:d}","{missing}",StringComparison.Ordinal);
        EditorAccepts("module",json).Should().BeTrue(); RuntimeAccepts("module",json).Should().BeFalse();
    }
}
