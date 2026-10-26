#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Extensibility.Context;
using AgentMemory.McpHost.Coding;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>Coding packs CMP-1: the anchors coding mode sends with recall, from the API, the prompt and the working tree.</summary>
public sealed class CodingAnchorsTests
{
    [Fact]
    public void A_prompt_yields_the_paths_and_code_names_it_mentions_not_its_plain_words()
    {
        var anchors = CodingAnchors.FromPrompt(
            "Removing a pack is slow. Make `RemovePackAsync` in src\\AgentMemory.Extensibility.Neo4j/Storage/Neo4jModuleStore.cs delete instead; see README.md and Neo4jModuleStore. HTTP is fine.");

        anchors.Select(a => (a.Kind, a.Value)).Should().Equal(
            (ContextAnchorKind.Symbol, "RemovePackAsync"),
            (ContextAnchorKind.Path, "src/AgentMemory.Extensibility.Neo4j/Storage/Neo4jModuleStore.cs"),
            (ContextAnchorKind.Path, "README.md"),
            (ContextAnchorKind.Symbol, "Neo4jModuleStore"));
        anchors.Should().OnlyContain(a => a.Source == "prompt");
    }

    [Fact]
    public void Git_status_yields_changed_and_untracked_files_with_renames_at_their_new_path()
    {
        const string porcelain = " M src/Store/ModuleStore.cs\nR  old/Name.cs -> src/New/Name.cs\n?? tests/NewTests.cs\n?? build/\n";

        CodingAnchors.FromStatus(porcelain).Select(a => a.Value).Should().Equal("src/Store/ModuleStore.cs", "src/New/Name.cs", "tests/NewTests.cs");
        CodingAnchors.FromStatus(string.Join("\n", Enumerable.Range(0, 40).Select(i => $" M f{i}.cs"))).Should().HaveCount(16);
    }

    [Fact]
    public void Api_anchors_are_validated_and_deduplicated()
    {
        CodingAnchors.From(null).Should().BeEmpty();
        CodingAnchors.From([new() { Kind = "path", Value = "./src/A.cs" }, new() { Kind = "path", Value = "src/A.cs" }, new() { Kind = "symbol", Value = "A" }])
            .Select(a => (a.Kind, a.Value)).Should().Equal((ContextAnchorKind.Path, "src/A.cs"), (ContextAnchorKind.Symbol, "A"));
        FluentActions.Invoking(() => CodingAnchors.From([new() { Kind = "url", Value = "x" }])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => CodingAnchors.From([.. Enumerable.Range(0, 33).Select(i => new CodingAnchorInput { Kind = "path", Value = $"f{i}" })]))
            .Should().Throw<ArgumentException>();
        CodingAnchors.Question([ContextAnchor.Path("src/A.cs")]).Should().Be("Before changing src/A.cs: what decisions, requirements, tests and lessons govern it?");
    }

    [Fact]
    public void Recall_takes_optional_anchors_and_governs_requires_them()
    {
        var recall = CodingToolDefinitions.All.Single(t => t.Name == "recall").InputSchema;
        recall.GetProperty("properties").TryGetProperty("anchors", out var anchors).Should().BeTrue();
        anchors.GetProperty("maxItems").GetInt32().Should().Be(32);
        recall.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Should().NotContain("anchors");
        CodingToolDefinitions.All.Single(t => t.Name == "governs").InputSchema.GetProperty("required").EnumerateArray()
            .Select(r => r.GetString()).Should().Equal("session", "anchors");
    }
}
