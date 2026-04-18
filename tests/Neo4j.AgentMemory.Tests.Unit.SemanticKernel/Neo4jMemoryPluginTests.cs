using FluentAssertions;
using Microsoft.SemanticKernel;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.SemanticKernel;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.SemanticKernel;

public sealed class Neo4jMemoryPluginTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly Neo4jMemoryPlugin _sut;

    public Neo4jMemoryPluginTests()
    {
        _sut = new Neo4jMemoryPlugin(_memoryService);
    }

    [Fact]
    public void FormatRecallResult_EmptyContext_ReturnsEmptyString()
    {
        var result = EmptyRecall("s1");
        Neo4jMemoryPlugin.FormatRecallResult(result).Should().BeEmpty();
    }

    [Fact]
    public void FormatRecallResult_WithRecentMessages_IncludesMessages()
    {
        var result = RecallWithMessages("s1");
        var formatted = Neo4jMemoryPlugin.FormatRecallResult(result);
        formatted.Should().Contain("[user]: Hello world");
        formatted.Should().Contain("Recent Messages");
    }

    [Fact]
    public void FormatRecallResult_WithEntities_IncludesEntitySection()
    {
        var result = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1", AssembledAtUtc = DateTimeOffset.UtcNow,
                RelevantEntities = new MemoryContextSection<Entity>
                {
                    Items = [ new Entity { EntityId = "e1", Name = "Neo4j", Type = "Organization",
                        Description = "Graph database company", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow } ]
                }
            },
            TotalItemsRetrieved = 1
        };
        var formatted = Neo4jMemoryPlugin.FormatRecallResult(result);
        formatted.Should().Contain("Known Entities").And.Contain("Neo4j (Organization)").And.Contain("Graph database company");
    }

    [Fact]
    public void FormatRecallResult_WithFacts_IncludesFactSection()
    {
        var result = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1", AssembledAtUtc = DateTimeOffset.UtcNow,
                RelevantFacts = new MemoryContextSection<Fact>
                {
                    Items = [ new Fact { FactId = "f1", Subject = "Neo4j", Predicate = "is", Object = "a graph database",
                        Confidence = 0.95, CreatedAtUtc = DateTimeOffset.UtcNow } ]
                }
            },
            TotalItemsRetrieved = 1
        };
        var formatted = Neo4jMemoryPlugin.FormatRecallResult(result);
        formatted.Should().Contain("Known Facts").And.Contain("Neo4j is a graph database");
    }

    [Fact]
    public void FormatRecallResult_WithPreferences_IncludesPreferencesSection()
    {
        var result = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1", AssembledAtUtc = DateTimeOffset.UtcNow,
                RelevantPreferences = new MemoryContextSection<Preference>
                {
                    Items = [ new Preference { PreferenceId = "p1", Category = "style",
                        PreferenceText = "Prefers dark mode", Confidence = 0.8, CreatedAtUtc = DateTimeOffset.UtcNow } ]
                }
            },
            TotalItemsRetrieved = 1
        };
        var formatted = Neo4jMemoryPlugin.FormatRecallResult(result);
        formatted.Should().Contain("User Preferences").And.Contain("[style] Prefers dark mode");
    }

    [Fact]
    public void FormatRecallResult_WithGraphRagContext_IncludesGraphSection()
    {
        var result = new RecallResult
        {
            Context = new MemoryContext { SessionId = "s1", AssembledAtUtc = DateTimeOffset.UtcNow, GraphRagContext = "GraphRAG summary here" },
            TotalItemsRetrieved = 1
        };
        var formatted = Neo4jMemoryPlugin.FormatRecallResult(result);
        formatted.Should().Contain("Graph Context").And.Contain("GraphRAG summary here");
    }

    [Fact]
    public async Task RecallAsync_CallsMemoryService_WithCorrectRequest()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));
        await _sut.RecallAsync("what is neo4j", "s1");
        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.SessionId == "s1" && r.Query == "what is neo4j"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsync_EmptyResult_ReturnsEmptyString()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));
        var result = await _sut.RecallAsync("query", "s1");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RecallAsync_WithMessages_ReturnsFormattedString()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(RecallWithMessages("s1"));
        var result = await _sut.RecallAsync("hello", "s1");
        result.Should().Contain("Hello world").And.Contain("[user]");
    }

    [Fact]
    public async Task RecallAsync_ServiceThrows_ReturnsEmptyString()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));
        var result = await _sut.RecallAsync("query", "s1");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task AddMessageAsync_DelegatesToService()
    {
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(MakeMessage("s1", "c1", "user", "Hello"));
        await _sut.AddMessageAsync("s1", "c1", "user", "Hello");
        await _memoryService.Received(1).AddMessageAsync("s1", "c1", "user", "Hello", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractFromSessionAsync_DelegatesToService()
    {
        _memoryService.ExtractFromSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await _sut.ExtractFromSessionAsync("s1");
        await _memoryService.Received(1).ExtractFromSessionAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractFromConversationAsync_DelegatesToService()
    {
        _memoryService.ExtractFromConversationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await _sut.ExtractFromConversationAsync("c1");
        await _memoryService.Received(1).ExtractFromConversationAsync("c1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearSessionAsync_DelegatesToService()
    {
        _memoryService.ClearSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await _sut.ClearSessionAsync("s1");
        await _memoryService.Received(1).ClearSessionAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Plugin_HasExpectedKernelFunctions()
    {
        var plugin = KernelPluginFactory.CreateFromObject(_sut, "Neo4jMemory");
        plugin.Name.Should().Be("Neo4jMemory");
        plugin.TryGetFunction("recall", out _).Should().BeTrue();
        plugin.TryGetFunction("add_message", out _).Should().BeTrue();
        plugin.TryGetFunction("extract_from_session", out _).Should().BeTrue();
        plugin.TryGetFunction("extract_from_conversation", out _).Should().BeTrue();
        plugin.TryGetFunction("clear_session", out _).Should().BeTrue();
    }

    private static RecallResult EmptyRecall(string sessionId) => new()
    {
        Context = new MemoryContext { SessionId = sessionId, AssembledAtUtc = DateTimeOffset.UtcNow },
        TotalItemsRetrieved = 0
    };

    private static RecallResult RecallWithMessages(string sessionId) => new()
    {
        Context = new MemoryContext
        {
            SessionId = sessionId, AssembledAtUtc = DateTimeOffset.UtcNow,
            RecentMessages = new MemoryContextSection<Message> { Items = [MakeMessage(sessionId, "c1", "user", "Hello world")] }
        },
        TotalItemsRetrieved = 1
    };

    private static Message MakeMessage(string sessionId, string conversationId, string role, string content) => new()
    {
        MessageId = Guid.NewGuid().ToString(), SessionId = sessionId, ConversationId = conversationId,
        Role = role, Content = content, TimestampUtc = DateTimeOffset.UtcNow
    };
}
