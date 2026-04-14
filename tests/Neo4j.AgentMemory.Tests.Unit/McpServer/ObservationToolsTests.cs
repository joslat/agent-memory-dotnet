using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer;
using Neo4j.AgentMemory.McpServer.Tools;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.McpServer;

public sealed class ObservationToolsTests
{
    private readonly IShortTermMemoryService _shortTermMemory = Substitute.For<IShortTermMemoryService>();
    private readonly IContextCompressor _compressor = Substitute.For<IContextCompressor>();
    private readonly IOptions<McpServerOptions> _options = Options.Create(new McpServerOptions());

    private static readonly DateTimeOffset FixedTime = new(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private static Message CreateMessage(string id, string content, string role = "user") => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "test-session",
        Role = role,
        Content = content,
        TimestampUtc = FixedTime
    };

    // ── Empty session ──

    [Fact]
    public async Task MemoryGetObservations_ReturnsEmptyForSessionWithNoMessages()
    {
        _shortTermMemory.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        var result = await ObservationTools.MemoryGetObservations(
            _shortTermMemory, _compressor, _options, "empty-session");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("empty-session");
        doc.RootElement.GetProperty("wasCompressed").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("recentMessageCount").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("originalTokenCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task MemoryGetObservations_DoesNotCallCompressorForEmptySession()
    {
        _shortTermMemory.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        await ObservationTools.MemoryGetObservations(
            _shortTermMemory, _compressor, _options, "empty-session");

        await _compressor.DidNotReceive().CompressAsync(
            Arg.Any<IReadOnlyList<Message>>(),
            Arg.Any<ContextCompressionOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Compression ──

    [Fact]
    public async Task MemoryGetObservations_CallsCompressorWithMessagesAndOptions()
    {
        var messages = new[] { CreateMessage("m1", "Hello"), CreateMessage("m2", "World") };
        _shortTermMemory.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(messages);

        _compressor.CompressAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ContextCompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new CompressedContext
            {
                WasCompressed = true,
                OriginalTokenCount = 500,
                CompressedTokenCount = 200,
                Observations = new[] { "User discussed greetings" },
                Reflections = new[] { "Session focused on introductions" }
            });

        var result = await ObservationTools.MemoryGetObservations(
            _shortTermMemory, _compressor, _options, "test-session", maxTokens: 2000);

        await _compressor.Received(1).CompressAsync(
            Arg.Is<IReadOnlyList<Message>>(m => m.Count == 2),
            Arg.Is<ContextCompressionOptions>(o => o.TokenThreshold == 2000),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryGetObservations_ReturnsCompressedResultWithObservations()
    {
        var messages = new[] { CreateMessage("m1", "Hello") };
        _shortTermMemory.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(messages);

        _compressor.CompressAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ContextCompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new CompressedContext
            {
                WasCompressed = true,
                OriginalTokenCount = 500,
                CompressedTokenCount = 200,
                Observations = new[] { "User greeted" },
                Reflections = new[] { "Introductory session" },
                RecentMessages = messages
            });

        var result = await ObservationTools.MemoryGetObservations(
            _shortTermMemory, _compressor, _options, "test-session");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("wasCompressed").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("originalTokenCount").GetInt32().Should().Be(500);
        doc.RootElement.GetProperty("compressedTokenCount").GetInt32().Should().Be(200);
        doc.RootElement.GetProperty("observations").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("reflections").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("recentMessageCount").GetInt32().Should().Be(1);
    }

    // ── Include flags ──

    [Fact]
    public async Task MemoryGetObservations_RespectsIncludeFlags()
    {
        _shortTermMemory.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        var result = await ObservationTools.MemoryGetObservations(
            _shortTermMemory, _compressor, _options,
            includeEntities: true, includeFacts: false, includePreferences: false);

        var doc = JsonDocument.Parse(result);
        var sections = doc.RootElement.GetProperty("includedSections");
        sections.GetArrayLength().Should().Be(1);
        sections[0].GetString().Should().Be("entities");
    }

    [Fact]
    public async Task MemoryGetObservations_AllIncludeFlagsTrue_IncludesAllSections()
    {
        _shortTermMemory.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        var result = await ObservationTools.MemoryGetObservations(
            _shortTermMemory, _compressor, _options,
            includeEntities: true, includeFacts: true, includePreferences: true);

        var doc = JsonDocument.Parse(result);
        var sections = doc.RootElement.GetProperty("includedSections");
        sections.GetArrayLength().Should().Be(3);
    }

    // ── Default session ──

    [Fact]
    public async Task MemoryGetObservations_UsesDefaultSessionIdWhenNoneProvided()
    {
        _shortTermMemory.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        var result = await ObservationTools.MemoryGetObservations(
            _shortTermMemory, _compressor, _options);

        await _shortTermMemory.Received(1).GetRecentMessagesAsync(
            "default", Arg.Any<int>(), Arg.Any<CancellationToken>());

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("default");
    }

    // ── Formatted summary ──

    [Fact]
    public async Task MemoryGetObservations_IncludesFormattedSummaryWhenCompressed()
    {
        var messages = new[] { CreateMessage("m1", "Test message") };
        _shortTermMemory.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(messages);

        _compressor.CompressAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ContextCompressionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new CompressedContext
            {
                WasCompressed = true,
                OriginalTokenCount = 100,
                CompressedTokenCount = 50,
                Observations = new[] { "Test observation" },
                Reflections = new[] { "Test reflection" },
                RecentMessages = messages
            });

        var result = await ObservationTools.MemoryGetObservations(
            _shortTermMemory, _compressor, _options, "sess-1");

        var doc = JsonDocument.Parse(result);
        var summary = doc.RootElement.GetProperty("formattedSummary").GetString()!;
        summary.Should().Contain("Memory Observations for session 'sess-1'");
        summary.Should().Contain("Test reflection");
        summary.Should().Contain("Test observation");
    }
}
