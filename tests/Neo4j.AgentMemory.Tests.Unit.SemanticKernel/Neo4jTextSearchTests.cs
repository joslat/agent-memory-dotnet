using FluentAssertions;
using Microsoft.SemanticKernel.Data;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.SemanticKernel;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

#pragma warning disable SKEXP0001

namespace Neo4j.AgentMemory.Tests.Unit.SemanticKernel;

public sealed class Neo4jTextSearchTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private const string SessionId = "test-session";
    private readonly Neo4jTextSearch _sut;

    public Neo4jTextSearchTests()
    {
        _sut = new Neo4jTextSearch(_memoryService, SessionId);
    }

    [Fact]
    public async Task SearchAsync_EmptyRecall_ReturnsEmptyResults()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall());
        var results = await _sut.SearchAsync("query");
        results.TotalCount.Should().Be(0);
        (await results.Results.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithMessages_ReturnsSingleFormattedString()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(RecallWithMessages());
        var items = await (await _sut.SearchAsync("hello")).Results.ToListAsync();
        items.Should().HaveCount(1);
        items[0].Should().Contain("Hello world");
    }

    [Fact]
    public async Task SearchAsync_ServiceThrows_ReturnsEmptyResults()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));
        var results = await _sut.SearchAsync("query");
        results.TotalCount.Should().Be(0);
        (await results.Results.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_UsesCorrectSessionId()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall());
        await _sut.SearchAsync("query");
        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.SessionId == SessionId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTextSearchResultsAsync_EmptyRecall_ReturnsEmptyResults()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall());
        var items = await (await _sut.GetTextSearchResultsAsync("query")).Results.ToListAsync();
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTextSearchResultsAsync_WithMessages_ReturnsTextSearchResults()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(RecallWithMessages());
        var items = await (await _sut.GetTextSearchResultsAsync("query")).Results.ToListAsync();
        items.Should().HaveCount(1);
        items[0].Value.Should().Be("Hello world");
        items[0].Name.Should().Be("user");
    }

    [Fact]
    public async Task GetTextSearchResultsAsync_WithEntities_ReturnsEntityResults()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(RecallWithEntities());
        var items = await (await _sut.GetTextSearchResultsAsync("query")).Results.ToListAsync();
        items.Should().HaveCount(1);
        items[0].Name.Should().Be("Neo4j");
    }

    [Fact]
    public async Task GetTextSearchResultsAsync_WithFacts_ReturnsFactResults()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(RecallWithFacts());
        var items = await (await _sut.GetTextSearchResultsAsync("query")).Results.ToListAsync();
        items.Should().HaveCount(1);
        items[0].Value.Should().Contain("is").And.Contain("graph database");
        items[0].Name.Should().Be("Neo4j");
    }

    [Fact]
    public async Task GetTextSearchResultsAsync_WithPreferences_ReturnsPreferenceResults()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(RecallWithPreferences());
        var items = await (await _sut.GetTextSearchResultsAsync("query")).Results.ToListAsync();
        items.Should().HaveCount(1);
        items[0].Value.Should().Be("Prefers dark mode");
        items[0].Name.Should().Be("style");
    }

    [Fact]
    public async Task GetSearchResultsAsync_WithMessages_ReturnsTextSearchResults()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(RecallWithMessages());
        var items = await (await _sut.GetSearchResultsAsync("query")).Results.ToListAsync();
        items.Should().HaveCount(1);
        items[0].Should().BeOfType<TextSearchResult>();
        ((TextSearchResult)items[0]).Value.Should().Be("Hello world");
    }

    private static RecallResult EmptyRecall() => new()
    {
        Context = new MemoryContext { SessionId = SessionId, AssembledAtUtc = DateTimeOffset.UtcNow },
        TotalItemsRetrieved = 0
    };

    private static RecallResult RecallWithMessages() => new()
    {
        Context = new MemoryContext
        {
            SessionId = SessionId, AssembledAtUtc = DateTimeOffset.UtcNow,
            RecentMessages = new MemoryContextSection<Message> { Items = [MakeMessage("user", "Hello world")] }
        },
        TotalItemsRetrieved = 1
    };

    private static RecallResult RecallWithEntities() => new()
    {
        Context = new MemoryContext
        {
            SessionId = SessionId, AssembledAtUtc = DateTimeOffset.UtcNow,
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = [ new Entity { EntityId = "e1", Name = "Neo4j", Type = "Organization",
                    Description = "Graph database", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow } ]
            }
        },
        TotalItemsRetrieved = 1
    };

    private static RecallResult RecallWithFacts() => new()
    {
        Context = new MemoryContext
        {
            SessionId = SessionId, AssembledAtUtc = DateTimeOffset.UtcNow,
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = [ new Fact { FactId = "f1", Subject = "Neo4j", Predicate = "is", Object = "graph database",
                    Confidence = 0.95, CreatedAtUtc = DateTimeOffset.UtcNow } ]
            }
        },
        TotalItemsRetrieved = 1
    };

    private static RecallResult RecallWithPreferences() => new()
    {
        Context = new MemoryContext
        {
            SessionId = SessionId, AssembledAtUtc = DateTimeOffset.UtcNow,
            RelevantPreferences = new MemoryContextSection<Preference>
            {
                Items = [ new Preference { PreferenceId = "p1", Category = "style",
                    PreferenceText = "Prefers dark mode", Confidence = 0.8, CreatedAtUtc = DateTimeOffset.UtcNow } ]
            }
        },
        TotalItemsRetrieved = 1
    };

    private static Message MakeMessage(string role, string content) => new()
    {
        MessageId = Guid.NewGuid().ToString(), SessionId = SessionId, ConversationId = "c1",
        Role = role, Content = content, TimestampUtc = DateTimeOffset.UtcNow
    };
}
