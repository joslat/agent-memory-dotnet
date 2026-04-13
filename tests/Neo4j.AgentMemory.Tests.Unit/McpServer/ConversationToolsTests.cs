using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer;
using Neo4j.AgentMemory.McpServer.Tools;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.McpServer;

public sealed class ConversationToolsTests
{
    private readonly IShortTermMemoryService _shortTermMemory = Substitute.For<IShortTermMemoryService>();
    private readonly IConversationRepository _conversationRepo = Substitute.For<IConversationRepository>();
    private readonly IOptions<McpServerOptions> _options = Options.Create(new McpServerOptions());

    private static readonly DateTimeOffset FixedTime = new(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);

    // ── memory_get_conversation ──

    [Fact]
    public async Task MemoryGetConversation_CallsGetConversationMessagesAsync()
    {
        _shortTermMemory.GetConversationMessagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Message>());

        await ConversationTools.MemoryGetConversation(_shortTermMemory, "conv-1");

        await _shortTermMemory.Received(1).GetConversationMessagesAsync("conv-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryGetConversation_ReturnsJsonArray()
    {
        var messages = new List<Message>
        {
            new()
            {
                MessageId = "msg-1",
                ConversationId = "conv-1",
                SessionId = "ses-1",
                Role = "user",
                Content = "hello",
                TimestampUtc = FixedTime
            },
            new()
            {
                MessageId = "msg-2",
                ConversationId = "conv-1",
                SessionId = "ses-1",
                Role = "assistant",
                Content = "hi there",
                TimestampUtc = FixedTime
            }
        };
        _shortTermMemory.GetConversationMessagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(messages);

        var result = await ConversationTools.MemoryGetConversation(_shortTermMemory, "conv-1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement[0].GetProperty("messageId").GetString().Should().Be("msg-1");
        doc.RootElement[1].GetProperty("role").GetString().Should().Be("assistant");
    }

    [Fact]
    public async Task MemoryGetConversation_ReturnsEmptyArrayWhenNoMessages()
    {
        _shortTermMemory.GetConversationMessagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Message>());

        var result = await ConversationTools.MemoryGetConversation(_shortTermMemory, "conv-1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(0);
    }

    // ── memory_list_sessions ──

    [Fact]
    public async Task MemoryListSessions_CallsGetBySessionAsync()
    {
        _conversationRepo.GetBySessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Conversation>());

        await ConversationTools.MemoryListSessions(_conversationRepo, _options, "ses-1");

        await _conversationRepo.Received(1).GetBySessionAsync("ses-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryListSessions_UsesDefaultSessionIdWhenNoneProvided()
    {
        _conversationRepo.GetBySessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Conversation>());

        await ConversationTools.MemoryListSessions(_conversationRepo, _options);

        await _conversationRepo.Received(1).GetBySessionAsync("default", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryListSessions_ReturnsJsonArrayOfConversations()
    {
        var conversations = new List<Conversation>
        {
            new()
            {
                ConversationId = "conv-1",
                SessionId = "ses-1",
                UserId = "user-1",
                CreatedAtUtc = FixedTime,
                UpdatedAtUtc = FixedTime
            }
        };
        _conversationRepo.GetBySessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(conversations);

        var result = await ConversationTools.MemoryListSessions(_conversationRepo, _options, "ses-1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("conversationId").GetString().Should().Be("conv-1");
        doc.RootElement[0].GetProperty("sessionId").GetString().Should().Be("ses-1");
    }
}
