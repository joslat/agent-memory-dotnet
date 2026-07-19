using System.Text.Json;
using FluentAssertions;
using AgentMemory.McpServer.Nams.Tools;
using AgentMemory.Nams.Domain;
using AgentMemory.Tests.Unit.Nams;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class NamsEntityToolsTests
{
    private sealed class FakeNamsClient : ThrowingNamsClientStub
    {
        public Func<CancellationToken, Task<NamsEntityGraph>>? OnGetEntityGraph { get; set; }
        public Func<string, IReadOnlyList<string>, CancellationToken, Task<NamsGraphExpansion>>? OnExpandGraph { get; set; }
        public Func<string, string, string?, CancellationToken, Task<NamsCreateEntityResult>>? OnCreateEntity { get; set; }
        public Func<string, double?, bool?, CancellationToken, Task<NamsEntityFeedbackResult>>? OnSetEntityFeedback { get; set; }
        public IReadOnlyList<string>? LastExpandGraphLoadedIds { get; private set; }

        public override Task<NamsEntityGraph> GetEntityGraphAsync(CancellationToken cancellationToken) =>
            (OnGetEntityGraph ?? (_ => Task.FromResult(new NamsEntityGraph([], []))))(cancellationToken);

        public override Task<NamsGraphExpansion> ExpandGraphAsync(
            string nodeId, IReadOnlyList<string> loadedIds, CancellationToken cancellationToken)
        {
            LastExpandGraphLoadedIds = loadedIds;
            return (OnExpandGraph ?? ((_, _, _) => Task.FromResult(new NamsGraphExpansion([], [], null))))(nodeId, loadedIds, cancellationToken);
        }

        public override Task<NamsCreateEntityResult> CreateEntityAsync(
            string name, string type, string? description, CancellationToken cancellationToken) =>
            (OnCreateEntity ?? ((_, _, _, _) => Task.FromResult(new NamsCreateEntityResult("e1", "created", name, type, description, null, null, null))))
            (name, type, description, cancellationToken);

        public override Task<NamsEntityFeedbackResult> SetEntityFeedbackAsync(
            string entityId, double? userScore, bool? confirmed, CancellationToken cancellationToken) =>
            (OnSetEntityFeedback ?? ((_, _, _, _) => Task.FromResult(new NamsEntityFeedbackResult(entityId, true))))
            (entityId, userScore, confirmed, cancellationToken);
    }

    // ---- nams_entity_graph ----

    [Fact]
    public async Task NamsEntityGraph_ReturnsNodesAndEdges()
    {
        var client = new FakeNamsClient
        {
            OnGetEntityGraph = _ => Task.FromResult(new NamsEntityGraph(
                [new NamsEntity("e1", "Alice", "Person", null, 0.9, null, null, null, null)],
                [new NamsGraphEdge("e1|KNOWS|e2", "e1", "e2", "KNOWS", null, 0.8, null, null, null)]))
        };

        var json = await NamsEntityReadTools.NamsEntityGraph(client, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("nodes")[0].GetProperty("name").GetString().Should().Be("Alice");
        doc.RootElement.GetProperty("edges")[0].GetProperty("type").GetString().Should().Be("KNOWS");
    }

    // ---- nams_expand_graph ----

    [Fact]
    public async Task NamsExpandGraph_MissingNodeId_ReturnsError()
    {
        var json = await NamsEntityReadTools.NamsExpandGraph(new FakeNamsClient(), "  ", null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("nodeId");
    }

    [Fact]
    public async Task NamsExpandGraph_NoLoadedIds_PassesEmptyList()
    {
        var client = new FakeNamsClient();

        await NamsEntityReadTools.NamsExpandGraph(client, "n1", null, CancellationToken.None);

        client.LastExpandGraphLoadedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task NamsExpandGraph_CommaSeparatedLoadedIds_SplitsAndTrims()
    {
        var client = new FakeNamsClient();

        await NamsEntityReadTools.NamsExpandGraph(client, "n1", " n2, n3 ,n4", CancellationToken.None);

        client.LastExpandGraphLoadedIds.Should().BeEquivalentTo(["n2", "n3", "n4"]);
    }

    [Fact]
    public async Task NamsExpandGraph_ReturnsNodesEdgesAndTruncation()
    {
        var client = new FakeNamsClient
        {
            OnExpandGraph = (_, _, _) => Task.FromResult(new NamsGraphExpansion(
                [new NamsExpandNode("n1", ["Entity"], new Dictionary<string, JsonElement>())],
                [],
                new NamsExpandTruncation("n1", 5, 10)))
        };

        var json = await NamsEntityReadTools.NamsExpandGraph(client, "n1", null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("nodes")[0].GetProperty("id").GetString().Should().Be("n1");
        doc.RootElement.GetProperty("truncated").GetProperty("total").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task NamsExpandGraph_MessageLabeledNode_ElidesProperties()
    {
        // SECURITY regression test: a "Message" node's properties can carry raw, unescaped conversation
        // content (confirmed live, Phase 10e) -- this tool must never pass that through unfiltered.
        var properties = new Dictionary<string, JsonElement>
        {
            ["content"] = JsonDocument.Parse("\"raw unvetted message text\"").RootElement
        };
        var client = new FakeNamsClient
        {
            OnExpandGraph = (_, _, _) => Task.FromResult(new NamsGraphExpansion(
                [
                    new NamsExpandNode("msg-1", ["Message"], properties),
                    new NamsExpandNode("ent-1", ["Entity"], properties)
                ],
                [],
                null))
        };

        var json = await NamsEntityReadTools.NamsExpandGraph(client, "n1", null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        nodes.Single(n => n.GetProperty("id").GetString() == "msg-1").TryGetProperty("properties", out _).Should().BeFalse();
        nodes.Single(n => n.GetProperty("id").GetString() == "ent-1").GetProperty("properties").GetProperty("content").GetString()
            .Should().Be("raw unvetted message text");
    }

    [Theory]
    [InlineData("Observation")]
    [InlineData("Reflection")]
    public async Task NamsExpandGraph_ObservationOrReflectionLabeledNode_ElidesProperties(string label)
    {
        // Regression test: broadened denylist -- Observation/Reflection are the same raw-content-carrying
        // categories NamsRecallCategory enumerates for the recall pipeline, even though only "Message" has
        // been live-confirmed as an actual expand-graph label so far.
        var properties = new Dictionary<string, JsonElement>
        {
            ["content"] = JsonDocument.Parse("\"raw unvetted content\"").RootElement
        };
        var client = new FakeNamsClient
        {
            OnExpandGraph = (_, _, _) => Task.FromResult(new NamsGraphExpansion(
                [new NamsExpandNode("n-1", [label], properties)],
                [],
                null))
        };

        var json = await NamsEntityReadTools.NamsExpandGraph(client, "n1", null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("nodes")[0].TryGetProperty("properties", out _).Should().BeFalse();
    }

    // ---- nams_create_entity ----

    [Theory]
    [InlineData("", "Person")]
    [InlineData("Alice", "")]
    public async Task NamsCreateEntity_MissingRequiredField_ReturnsError(string name, string type)
    {
        var json = await NamsEntityWriteTools.NamsCreateEntity(new FakeNamsClient(), name, type, null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task NamsCreateEntity_MergedResolution_OmitsNameAndType()
    {
        var client = new FakeNamsClient
        {
            OnCreateEntity = (_, _, _, _) => Task.FromResult(new NamsCreateEntityResult(
                "existing-1", "merged", null, null, null, null, "existing-1", 0.95))
        };

        var json = await NamsEntityWriteTools.NamsCreateEntity(client, "Alice", "Person", "desc", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("resolution").GetString().Should().Be("merged");
        doc.RootElement.GetProperty("mergedInto").GetString().Should().Be("existing-1");
        doc.RootElement.TryGetProperty("name", out _).Should().BeFalse(); // WhenWritingNull elides it
    }

    // ---- nams_entity_feedback ----

    [Fact]
    public async Task NamsEntityFeedback_MissingEntityId_ReturnsError()
    {
        var json = await NamsEntityWriteTools.NamsEntityFeedback(new FakeNamsClient(), " ", 0.5, true, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("entityId");
        doc.RootElement.GetProperty("updated").GetBoolean().Should().BeFalse(); // matches nams_remember's stable-key-on-error convention
    }

    [Fact]
    public async Task NamsEntityFeedback_ValidRequest_ReturnsUpdatedResult()
    {
        var client = new FakeNamsClient
        {
            OnSetEntityFeedback = (id, _, _, _) => Task.FromResult(new NamsEntityFeedbackResult(id, true))
        };

        var json = await NamsEntityWriteTools.NamsEntityFeedback(client, "e1", 0.7, true, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetString().Should().Be("e1");
        doc.RootElement.GetProperty("updated").GetBoolean().Should().BeTrue();
    }
}
