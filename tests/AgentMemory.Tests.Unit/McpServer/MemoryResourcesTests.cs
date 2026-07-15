using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.McpServer.Resources;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class MemoryResourcesTests
{
    private readonly IGraphQueryService _graphQueryService = Substitute.For<IGraphQueryService>();

    // SingleTenant (default) reproduces this file's pre-#100 behavior exactly: a null/blank userId is
    // unscoped, not a failure -- these tests target the WHERE-clause/parameter shape, not isolation-mode
    // behavior itself (that's DefaultMemoryIsolationPolicyTests' job).
    private static readonly IMemoryIsolationPolicy IsolationPolicy =
        new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance);

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

        var result = await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy);

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

        var result = await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy);

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

        await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy, limit: 10, offset: 5);

        var doc = await CaptureEntityListResult(10, 5);
        doc.RootElement.GetProperty("limit").GetInt32().Should().Be(10);
        doc.RootElement.GetProperty("offset").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task EntityList_ReturnsEmptyArrayWhenNoEntities()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        var result = await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("entities").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task EntityList_WithUserId_OwnerScopesQuery()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy, userId: "alice");

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Is<string>(q => q.Contains("(e.owner_id = $ownerId OR e.owner_id IS NULL)")),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && (string?)p["ownerId"] == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EntityList_WithoutUserId_IsNotOwnerScoped()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy);

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Is<string>(q => !q.Contains("owner_id")),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && !p.ContainsKey("ownerId")),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════
    //  PreferenceListResource
    // ═══════════════════════════════

    [Fact]
    public async Task PreferenceList_WithUserId_OwnerScopesQuery()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await PreferenceListResource.GetPreferences(_graphQueryService, IsolationPolicy, userId: "alice");

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Is<string>(q => q.Contains("(p.owner_id = $ownerId OR p.owner_id IS NULL)")),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && (string?)p["ownerId"] == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreferenceList_WithoutUserId_IsNotOwnerScoped()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await PreferenceListResource.GetPreferences(_graphQueryService, IsolationPolicy);

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Is<string>(q => !q.Contains("owner_id")),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && !p.ContainsKey("ownerId")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreferenceList_ReturnsPreferenceFields()
    {
        _graphQueryService.QueryAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "p1", ["preference"] = "dark mode", ["category"] = "style",
                    ["context"] = "always", ["confidence"] = 0.9, ["createdAt"] = "2025-01-15T10:00:00Z"
                }
            });

        var result = await PreferenceListResource.GetPreferences(_graphQueryService, IsolationPolicy);

        var doc = JsonDocument.Parse(result);
        var prefs = doc.RootElement.GetProperty("preferences");
        prefs.GetArrayLength().Should().Be(1);
        prefs[0].GetProperty("preference").GetString().Should().Be("dark mode");
        prefs[0].GetProperty("category").GetString().Should().Be("style");
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

        var result = await ConversationListResource.GetConversations(_graphQueryService, IsolationPolicy);

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

        var result = await ConversationListResource.GetConversations(_graphQueryService, IsolationPolicy);

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

        var result = await ConversationListResource.GetConversations(_graphQueryService, IsolationPolicy, limit: 5);

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && (long)p["limit"]! == 5L),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationList_WithUserId_ScopesByUserId()
    {
        SetupConversationQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await ConversationListResource.GetConversations(_graphQueryService, IsolationPolicy, userId: "alice");

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Is<string>(q => q.Contains("(c.user_id = $userId OR c.user_id IS NULL)")),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && (string?)p["userId"] == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationList_WithoutUserId_IsNotScoped()
    {
        SetupConversationQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await ConversationListResource.GetConversations(_graphQueryService, IsolationPolicy);

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Is<string>(q => !q.Contains("user_id")),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && !p.ContainsKey("userId")),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════
    //  ContextResource (leak-1: owner-scoped recall)
    // ═══════════════════════════════

    [Fact]
    public async Task Context_WithUserId_PassesOwnerScopedRecallRequest()
    {
        var assembler = Substitute.For<IMemoryContextAssembler>();
        assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryContext { SessionId = "s1", AssembledAtUtc = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero) });

        await ContextResource.GetContext(assembler, "s1", query: "hello", userId: "alice");

        await assembler.Received(1).AssembleContextAsync(
            Arg.Is<RecallRequest>(r => r.UserId == "alice" && r.SessionId == "s1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Context_WithoutUserId_PassesUnscopedRecallRequest()
    {
        var assembler = Substitute.For<IMemoryContextAssembler>();
        assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryContext { SessionId = "s1", AssembledAtUtc = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero) });

        await ContextResource.GetContext(assembler, "s1", query: "hello");

        await assembler.Received(1).AssembleContextAsync(
            Arg.Is<RecallRequest>(r => r.UserId == null),
            Arg.Any<CancellationToken>());
    }

    // ── EntityList / PreferenceList combined-filter + blank-userId edges (test-2) ──

    [Fact]
    public async Task EntityList_WithTypeAndUserId_JoinsBothPredicatesWithAnd()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy, type: "PERSON", userId: "alice");

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Is<string>(q => q.Contains("e.type = $type")
                                && q.Contains("(e.owner_id = $ownerId OR e.owner_id IS NULL)")
                                && q.Contains(" AND ")),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && (string?)p["ownerId"] == "alice" && (string?)p["type"] == "PERSON"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EntityList_EmptyUserId_IsUnscoped()
    {
        SetupEntityQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy, userId: "");

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Is<string>(q => !q.Contains("owner_id")),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(p => p != null && !p.ContainsKey("ownerId")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationList_ReturnsEmptyArrayWhenNoConversations()
    {
        SetupConversationQuery(Array.Empty<IReadOnlyDictionary<string, object?>>());

        var result = await ConversationListResource.GetConversations(_graphQueryService, IsolationPolicy);

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
        var result = await EntityListResource.GetEntities(_graphQueryService, IsolationPolicy, limit: limit, offset: offset);
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
