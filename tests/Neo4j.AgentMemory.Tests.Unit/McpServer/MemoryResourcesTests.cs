using System.Text.Json;
using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer.Resources;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.McpServer;

public sealed class MemoryResourcesTests
{
    private readonly IGraphQueryService _graphQueryService = Substitute.For<IGraphQueryService>();

    // ═══════════════════════════════
    //  MemoryStatusResource
    // ═══════════════════════════════

    [Fact]
    public async Task MemoryStatus_ReturnsValidJson()
    {
        SetupStatusQuery(10, 5, 3, 2, 20);

        var result = await MemoryStatusResource.GetMemoryStatus(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task MemoryStatus_IncludesAllCounts()
    {
        SetupStatusQuery(10, 5, 3, 2, 20);

        var result = await MemoryStatusResource.GetMemoryStatus(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("entityCount").GetInt64().Should().Be(10);
        doc.RootElement.GetProperty("factCount").GetInt64().Should().Be(5);
        doc.RootElement.GetProperty("preferenceCount").GetInt64().Should().Be(3);
        doc.RootElement.GetProperty("conversationCount").GetInt64().Should().Be(2);
        doc.RootElement.GetProperty("messageCount").GetInt64().Should().Be(20);
    }

    [Fact]
    public async Task MemoryStatus_IncludesTimestamp()
    {
        SetupStatusQuery(0, 0, 0, 0, 0);

        var result = await MemoryStatusResource.GetMemoryStatus(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("retrievedAtUtc", out _).Should().BeTrue();
    }

    [Fact]
    public async Task MemoryStatus_ReturnsZerosWhenNoData()
    {
        _graphQueryService.QueryAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        var result = await MemoryStatusResource.GetMemoryStatus(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("entityCount").GetInt64().Should().Be(0);
        doc.RootElement.GetProperty("messageCount").GetInt64().Should().Be(0);
    }

    // ═══════════════════════════════
    //  EntityListResource
    // ═══════════════════════════════

    [Fact]
    public async Task EntityList_ReturnsValidJson()
    {
        SetupEntityQuery(new[]
        {
            CreateEntityRow("e1", "Alice", "PERSON", 2),
            CreateEntityRow("e2", "Bob", "PERSON", 0)
        });

        var result = await EntityListResource.GetEntities(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task EntityList_ReturnsEntitiesWithExpectedFields()
    {
        SetupEntityQuery(new[]
        {
            CreateEntityRow("e1", "Alice", "PERSON", 2)
        });

        var result = await EntityListResource.GetEntities(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        var entities = doc.RootElement.GetProperty("entities");
        entities.GetArrayLength().Should().Be(1);
        entities[0].GetProperty("id").GetString().Should().Be("e1");
        entities[0].GetProperty("name").GetString().Should().Be("Alice");
        entities[0].GetProperty("type").GetString().Should().Be("PERSON");
        entities[0].GetProperty("aliasCount").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task EntityList_RespectsLimitParameter()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await EntityListResource.GetEntities(_graphQueryService, limit: 10, offset: 5);

        var doc = await CaptureEntityListResult(10, 5);
        doc.RootElement.GetProperty("limit").GetInt32().Should().Be(10);
        doc.RootElement.GetProperty("offset").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task EntityList_ReturnsEmptyArrayWhenNoEntities()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        var result = await EntityListResource.GetEntities(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("entities").GetArrayLength().Should().Be(0);
    }

    // ═══════════════════════════════
    //  ConversationListResource
    // ═══════════════════════════════

    [Fact]
    public async Task ConversationList_ReturnsValidJson()
    {
        SetupConversationQuery(new[]
        {
            CreateConversationRow("c1", "sess-1", "2025-01-15T10:00:00Z", 5)
        });

        var result = await ConversationListResource.GetConversations(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task ConversationList_ReturnsConversationsWithExpectedFields()
    {
        SetupConversationQuery(new[]
        {
            CreateConversationRow("c1", "sess-1", "2025-01-15T10:00:00Z", 5)
        });

        var result = await ConversationListResource.GetConversations(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        var convs = doc.RootElement.GetProperty("conversations");
        convs.GetArrayLength().Should().Be(1);
        convs[0].GetProperty("id").GetString().Should().Be("c1");
        convs[0].GetProperty("sessionId").GetString().Should().Be("sess-1");
        convs[0].GetProperty("messageCount").GetInt64().Should().Be(5);
    }

    [Fact]
    public async Task ConversationList_RespectsLimitParameter()
    {
        SetupConversationQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        var result = await ConversationListResource.GetConversations(_graphQueryService, limit: 5);

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && (long)p["limit"]! == 5L),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationList_ReturnsEmptyArrayWhenNoConversations()
    {
        SetupConversationQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        var result = await ConversationListResource.GetConversations(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("conversations").GetArrayLength().Should().Be(0);
    }

    // ═══════════════════════════════
    //  SchemaInfoResource
    // ═══════════════════════════════

    [Fact]
    public async Task SchemaInfo_ReturnsValidJson()
    {
        SetupSchemaQueries(
            new object[] { "Entity", "Message" },
            new object[] { "KNOWS", "HAS_MESSAGE" },
            new object[] { "name", "content" });

        var result = await SchemaInfoResource.GetSchema(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task SchemaInfo_IncludesLabelsAndRelationships()
    {
        SetupSchemaQueries(
            new object[] { "Entity", "Fact" },
            new object[] { "KNOWS", "MENTIONS" },
            new object[] { "name", "type" });

        var result = await SchemaInfoResource.GetSchema(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("labels").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("relationshipTypes").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("propertyKeys").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task SchemaInfo_IncludesTimestamp()
    {
        SetupSchemaQueries(Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>());

        var result = await SchemaInfoResource.GetSchema(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("retrievedAtUtc", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SchemaInfo_ReturnsEmptyArraysWhenNoSchema()
    {
        SetupSchemaQueries(Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>());

        var result = await SchemaInfoResource.GetSchema(_graphQueryService);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("labels").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("relationshipTypes").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("propertyKeys").GetArrayLength().Should().Be(0);
    }

    // ═══════════════════════════════
    //  Helpers
    // ═══════════════════════════════

    private void SetupStatusQuery(long entities, long facts, long prefs, long convs, long msgs)
    {
        var row = new Dictionary<string, object?>
        {
            ["entityCount"] = entities,
            ["factCount"] = facts,
            ["preferenceCount"] = prefs,
            ["conversationCount"] = convs,
            ["messageCount"] = msgs
        };
        _graphQueryService.QueryAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>> { row });
    }

    private void SetupEntityQuery(IReadOnlyDictionary<string, object?>[] rows)
    {
        _graphQueryService.QueryAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>(rows));
    }

    private static Dictionary<string, object?> CreateEntityRow(string id, string name, string type, long aliasCount)
        => new()
        {
            ["id"] = id,
            ["name"] = name,
            ["type"] = type,
            ["aliasCount"] = aliasCount
        };

    private void SetupConversationQuery(IReadOnlyDictionary<string, object?>[] rows)
    {
        _graphQueryService.QueryAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>(rows));
    }

    private static Dictionary<string, object?> CreateConversationRow(string id, string sessionId, string createdAt, long messageCount)
        => new()
        {
            ["id"] = id,
            ["sessionId"] = sessionId,
            ["createdAt"] = createdAt,
            ["messageCount"] = messageCount
        };

    private async Task<JsonDocument> CaptureEntityListResult(int limit, int offset)
    {
        var result = await EntityListResource.GetEntities(_graphQueryService, limit: limit, offset: offset);
        return JsonDocument.Parse(result);
    }

    private void SetupSchemaQueries(object[] labels, object[] relTypes, object[] propKeys)
    {
        // The schema resource makes 3 sequential calls; use Returns with multiple returns
        _graphQueryService.QueryAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["labels"] = labels } },
                new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["relationshipTypes"] = relTypes } },
                new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["propertyKeys"] = propKeys } }
            );
    }
}
