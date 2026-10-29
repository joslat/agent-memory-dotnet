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

    internal static bool EditorAccepts(string kind, string json)
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
    [InlineData("snapshot", false, true)]
    [InlineData("snapshot", true, true)]
    [InlineData("history", false, true)]
    [InlineData("history", true, false)]
    [InlineData("History", false, false)]
    [InlineData("all", false, false)]
    public void HistorySelectionHasTheSameEditorAndRuntimeContract(string selection, bool sorted, bool accepted)
    {
        var root = JsonNode.Parse(ModuleHistoryReadTests.Manifest(selection))!;
        if (sorted) root["views"]![0]!["orderBy"] = "title";
        var json = root.ToJsonString();
        EditorAccepts("module", json).Should().Be(accepted);
        RuntimeAccepts("module", json).Should().Be(accepted);
    }

    // Review 1, H5: the schema had drifted (32 traversal relations where the runtime takes 16; a trial's upgrade refused).
    [Theory]
    [InlineData(16, null, true)]
    [InlineData(17, null, false)]
    [InlineData(2, 0.05, true)]
    [InlineData(2, 0.5, true)]
    [InlineData(2, 0.6, false)]
    public void TraversalAndTrialHaveTheSameEditorAndRuntimeContract(int relations, double? tolerance, bool accepted)
    {
        var names = Enumerable.Range(0, (relations + 1) / 2).Select(i => $"REVIEW_R{i}").ToList();
        var root = new JsonObject
        {
            ["format"] = "agentmemory.module/v1alpha1", ["id"] = "com.example.reviews", ["version"] = "1.0.0", ["sdk"] = "[0.1,0.2)",
            ["ontology"] = new JsonObject
            {
                ["labelPrefix"] = "Review",
                ["types"] = new JsonArray(new JsonObject { ["name"] = "ReviewItem", ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } }, ["render"] = "{path}" }),
                ["relations"] = new JsonArray([.. names.Select(n => (JsonNode)new JsonObject { ["name"] = n, ["from"] = "ReviewItem", ["to"] = "ReviewItem" })]),
            },
            ["traversal"] = new JsonObject
            {
                ["anchors"] = new JsonArray(new JsonObject { ["type"] = "ReviewItem", ["property"] = "path" }),
                ["relations"] = new JsonArray([.. names.SelectMany(n => new[] { "out", "in" }.Select(d => (JsonNode)new JsonObject { ["name"] = n, ["direction"] = d })).Take(relations)]),
            },
            ["trial"] = new JsonObject
            {
                ["measure"] = "test-impact", ["targets"] = new JsonObject { ["hitRate"] = 0.5 },
                ["upgrade"] = tolerance is { } t ? new JsonObject { ["noRegression"] = true, ["tolerance"] = t } : new JsonObject { ["noRegression"] = true },
            },
        };
        var json = root.ToJsonString();
        EditorAccepts("module", json).Should().Be(accepted);
        RuntimeAccepts("module", json).Should().Be(accepted);
    }

    // Review 2: the edges the schema and the validator must agree on, beyond the counts above.
    [Theory]
    [InlineData("anchors-16", true)]
    [InlineData("anchors-17", false)]
    [InlineData("tasks-9", false)]
    [InlineData("tasks-10", true)]
    [InlineData("tasks-200", true)]
    [InlineData("tasks-201", false)]
    [InlineData("tolerance-0", true)]
    [InlineData("tolerance-negative", false)]
    [InlineData("traversal-without-anchors-or-relations", false)]
    [InlineData("traversal-with-empty-anchors", false)]
    [InlineData("hops-written-2.0", true)]
    [InlineData("int-bound-written-2.0", true)]
    [InlineData("containable-by-not-an-id", false)]
    public void TraversalAndTrialEdgesHaveTheSameEditorAndRuntimeContract(string edge, bool accepted)
    {
        JsonNode Anchor(int i) => new JsonObject { ["type"] = "ReviewItem", ["property"] = "p" + i };
        var properties = new JsonObject();
        for (var i = 0; i < 17; i++) properties["p" + i] = new JsonObject { ["type"] = "string" };
        var traversal = new JsonObject { ["anchors"] = new JsonArray(Anchor(0)) };
        var trial = new JsonObject { ["measure"] = "test-impact", ["targets"] = new JsonObject { ["hitRate"] = 0.5 } };
        switch (edge)
        {
            case "anchors-16": traversal["anchors"] = new JsonArray([.. Enumerable.Range(0, 16).Select(Anchor)]); break;
            case "anchors-17": traversal["anchors"] = new JsonArray([.. Enumerable.Range(0, 17).Select(Anchor)]); break;
            case "tasks-9": trial["tasks"] = 9; break;
            case "tasks-10": trial["tasks"] = 10; break;
            case "tasks-200": trial["tasks"] = 200; break;
            case "tasks-201": trial["tasks"] = 201; break;
            case "tolerance-0": trial["upgrade"] = new JsonObject { ["noRegression"] = true, ["tolerance"] = 0 }; break;
            case "tolerance-negative": trial["upgrade"] = new JsonObject { ["noRegression"] = true, ["tolerance"] = -0.01 }; break;
            case "traversal-without-anchors-or-relations": traversal = new JsonObject { ["maxItems"] = 3 }; break;
            case "traversal-with-empty-anchors": traversal = new JsonObject { ["anchors"] = new JsonArray() }; break;
            case "hops-written-2.0": traversal["hops"] = JsonValue.Create(2.0m); break;
        }
        var count = new JsonObject { ["type"] = "int" };
        if (edge == "int-bound-written-2.0") { count["min"] = JsonValue.Create(2.0m); count["max"] = JsonValue.Create(9.0m); }
        properties["count"] = count;
        var root = new JsonObject
        {
            ["format"] = "agentmemory.module/v1alpha1", ["id"] = "com.example.reviews", ["version"] = "1.0.0", ["sdk"] = "[0.1,0.2)",
            ["ontology"] = new JsonObject
            {
                ["labelPrefix"] = "Review",
                ["types"] = new JsonArray(new JsonObject { ["name"] = "ReviewItem", ["properties"] = properties, ["render"] = "{p0}" }),
            },
            ["traversal"] = traversal, ["trial"] = trial,
        };
        if (edge == "containable-by-not-an-id") root["containableBy"] = new JsonArray("Not An Id");
        var json = root.ToJsonString();
        if (edge == "hops-written-2.0") json.Should().Contain("\"hops\":2.0");
        EditorAccepts("module", json).Should().Be(accepted, "the editor schema");
        RuntimeAccepts("module", json).Should().Be(accepted, "the runtime");
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
