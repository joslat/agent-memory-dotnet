using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer;
using Neo4j.AgentMemory.McpServer.Tools;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.McpServer;

public sealed class CoreMemoryToolsTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly ILongTermMemoryService _longTermMemory = Substitute.For<ILongTermMemoryService>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IOptions<McpServerOptions> _options = Options.Create(new McpServerOptions());

    private static readonly DateTimeOffset FixedTime = new(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public CoreMemoryToolsTests()
    {
        _idGenerator.GenerateId().Returns("generated-id-1");
        _clock.UtcNow.Returns(FixedTime);
    }

    private RecallResult CreateRecallResult(int totalItems = 5) => new()
    {
        TotalItemsRetrieved = totalItems,
        Truncated = false,
        EstimatedTokenCount = 100,
        Context = new MemoryContext
        {
            SessionId = "test-session",
            AssembledAtUtc = FixedTime
        }
    };

    // ── memory_search ──

    [Fact]
    public async Task MemorySearch_CallsRecallAsyncWithCorrectParameters()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateRecallResult());

        await CoreMemoryTools.MemorySearch(_memoryService, _options, "test query", "ses-1", "user-1");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r =>
                r.Query == "test query" &&
                r.SessionId == "ses-1" &&
                r.UserId == "user-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemorySearch_UsesDefaultSessionIdWhenNoneProvided()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateRecallResult());

        await CoreMemoryTools.MemorySearch(_memoryService, _options, "query");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.SessionId == "default"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemorySearch_ReturnsJsonWithExpectedStructure()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateRecallResult(5));

        var result = await CoreMemoryTools.MemorySearch(_memoryService, _options, "query");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("totalItemsRetrieved").GetInt32().Should().Be(5);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("estimatedTokenCount").GetInt32().Should().Be(100);
        doc.RootElement.GetProperty("context").GetProperty("sessionId").GetString().Should().Be("test-session");
    }

    // ── memory_get_context ──

    [Fact]
    public async Task MemoryGetContext_CallsRecallAsyncWithCorrectParameters()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateRecallResult());

        await CoreMemoryTools.MemoryGetContext(_memoryService, _options, "topic", "ses-2", "user-2");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r =>
                r.Query == "topic" &&
                r.SessionId == "ses-2" &&
                r.UserId == "user-2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryGetContext_ReturnsSerializedResult()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(CreateRecallResult(3));

        var result = await CoreMemoryTools.MemoryGetContext(_memoryService, _options, "topic");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("totalItemsRetrieved").GetInt32().Should().Be(3);
    }

    // ── memory_store_message ──

    [Fact]
    public async Task MemoryStoreMessage_CallsAddMessageAsyncWithCorrectParameters()
    {
        var msg = new Message
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            SessionId = "ses-1",
            Role = "user",
            Content = "hello",
            TimestampUtc = FixedTime
        };
        _memoryService.AddMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(msg);

        await CoreMemoryTools.MemoryStoreMessage(_memoryService, _options, "user", "hello", "ses-1", "conv-1");

        await _memoryService.Received(1).AddMessageAsync("ses-1", "conv-1", "user", "hello",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryStoreMessage_UsesDefaultSessionAndConversationIdWhenNoneProvided()
    {
        var msg = new Message
        {
            MessageId = "msg-1",
            ConversationId = "default",
            SessionId = "default",
            Role = "assistant",
            Content = "hi",
            TimestampUtc = FixedTime
        };
        _memoryService.AddMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(msg);

        await CoreMemoryTools.MemoryStoreMessage(_memoryService, _options, "assistant", "hi");

        await _memoryService.Received(1).AddMessageAsync("default", "default", "assistant", "hi",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryStoreMessage_ReturnsJsonWithMessageProperties()
    {
        var msg = new Message
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            SessionId = "ses-1",
            Role = "user",
            Content = "hello",
            TimestampUtc = FixedTime
        };
        _memoryService.AddMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(msg);

        var result = await CoreMemoryTools.MemoryStoreMessage(_memoryService, _options, "user", "hello", "ses-1", "conv-1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("messageId").GetString().Should().Be("msg-1");
        doc.RootElement.GetProperty("role").GetString().Should().Be("user");
        doc.RootElement.GetProperty("content").GetString().Should().Be("hello");
    }

    // ── memory_add_entity ──

    [Fact]
    public async Task MemoryAddEntity_CallsAddEntityAsyncWithCorrectProperties()
    {
        _longTermMemory.AddEntityAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Entity>());

        await CoreMemoryTools.MemoryAddEntity(
            _longTermMemory, _idGenerator, _clock, _options, "Alice", "Person", "A developer");

        await _longTermMemory.Received(1).AddEntityAsync(
            Arg.Is<Entity>(e =>
                e.EntityId == "generated-id-1" &&
                e.Name == "Alice" &&
                e.Type == "Person" &&
                e.Description == "A developer" &&
                e.CreatedAtUtc == FixedTime),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAddEntity_UsesDefaultConfidenceWhenNoneProvided()
    {
        _longTermMemory.AddEntityAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Entity>());

        await CoreMemoryTools.MemoryAddEntity(
            _longTermMemory, _idGenerator, _clock, _options, "Bob", "Person");

        await _longTermMemory.Received(1).AddEntityAsync(
            Arg.Is<Entity>(e => e.Confidence == 0.9),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAddEntity_UsesProvidedConfidenceWhenSpecified()
    {
        _longTermMemory.AddEntityAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Entity>());

        await CoreMemoryTools.MemoryAddEntity(
            _longTermMemory, _idGenerator, _clock, _options, "Bob", "Person", confidence: 0.75);

        await _longTermMemory.Received(1).AddEntityAsync(
            Arg.Is<Entity>(e => e.Confidence == 0.75),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAddEntity_ReturnsJsonWithEntityProperties()
    {
        _longTermMemory.AddEntityAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Entity>());

        var result = await CoreMemoryTools.MemoryAddEntity(
            _longTermMemory, _idGenerator, _clock, _options, "Alice", "Person", "A developer");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("entityId").GetString().Should().Be("generated-id-1");
        doc.RootElement.GetProperty("name").GetString().Should().Be("Alice");
        doc.RootElement.GetProperty("type").GetString().Should().Be("Person");
    }

    // ── memory_add_preference ──

    [Fact]
    public async Task MemoryAddPreference_CallsAddPreferenceAsyncWithCorrectProperties()
    {
        _longTermMemory.AddPreferenceAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Preference>());

        await CoreMemoryTools.MemoryAddPreference(
            _longTermMemory, _idGenerator, _clock, _options, "style", "dark mode", "IDE");

        await _longTermMemory.Received(1).AddPreferenceAsync(
            Arg.Is<Preference>(p =>
                p.PreferenceId == "generated-id-1" &&
                p.Category == "style" &&
                p.PreferenceText == "dark mode" &&
                p.Context == "IDE" &&
                p.CreatedAtUtc == FixedTime),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAddPreference_UsesDefaultConfidenceWhenNoneProvided()
    {
        _longTermMemory.AddPreferenceAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Preference>());

        await CoreMemoryTools.MemoryAddPreference(
            _longTermMemory, _idGenerator, _clock, _options, "style", "dark mode");

        await _longTermMemory.Received(1).AddPreferenceAsync(
            Arg.Is<Preference>(p => p.Confidence == 0.9),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAddPreference_ReturnsJsonWithPreferenceProperties()
    {
        _longTermMemory.AddPreferenceAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Preference>());

        var result = await CoreMemoryTools.MemoryAddPreference(
            _longTermMemory, _idGenerator, _clock, _options, "style", "dark mode", "IDE");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("preferenceId").GetString().Should().Be("generated-id-1");
        doc.RootElement.GetProperty("category").GetString().Should().Be("style");
        doc.RootElement.GetProperty("preferenceText").GetString().Should().Be("dark mode");
    }

    // ── memory_add_fact ──

    [Fact]
    public async Task MemoryAddFact_CallsAddFactAsyncWithCorrectProperties()
    {
        _longTermMemory.AddFactAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Fact>());

        await CoreMemoryTools.MemoryAddFact(
            _longTermMemory, _idGenerator, _clock, _options, "Alice", "works_at", "Microsoft");

        await _longTermMemory.Received(1).AddFactAsync(
            Arg.Is<Fact>(f =>
                f.FactId == "generated-id-1" &&
                f.Subject == "Alice" &&
                f.Predicate == "works_at" &&
                f.Object == "Microsoft" &&
                f.CreatedAtUtc == FixedTime),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAddFact_UsesDefaultConfidenceWhenNoneProvided()
    {
        _longTermMemory.AddFactAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Fact>());

        await CoreMemoryTools.MemoryAddFact(
            _longTermMemory, _idGenerator, _clock, _options, "Alice", "works_at", "Microsoft");

        await _longTermMemory.Received(1).AddFactAsync(
            Arg.Is<Fact>(f => f.Confidence == 0.9),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAddFact_ReturnsJsonWithFactProperties()
    {
        _longTermMemory.AddFactAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Fact>());

        var result = await CoreMemoryTools.MemoryAddFact(
            _longTermMemory, _idGenerator, _clock, _options, "Alice", "works_at", "Microsoft", 0.8);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("factId").GetString().Should().Be("generated-id-1");
        doc.RootElement.GetProperty("subject").GetString().Should().Be("Alice");
        doc.RootElement.GetProperty("predicate").GetString().Should().Be("works_at");
        doc.RootElement.GetProperty("object").GetString().Should().Be("Microsoft");
        doc.RootElement.GetProperty("confidence").GetDouble().Should().Be(0.8);
    }
}
