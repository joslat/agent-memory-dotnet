using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.McpServer;
using AgentMemory.McpServer.Tools;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class ConversationToolsTests
{
    private readonly IShortTermMemoryService _shortTermMemory = Substitute.For<IShortTermMemoryService>();
    private readonly IConversationRepository _conversationRepo = Substitute.For<IConversationRepository>();
    private readonly IOptions<AgentMemoryMcpOptions> _options = Options.Create(new AgentMemoryMcpOptions());
    private readonly IMemoryIsolationPolicy _isolationPolicy =
        new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private static readonly DateTimeOffset FixedTime = new(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);

    // ── memory_get_conversation ──

    [Fact]
    public async Task MemoryGetConversation_CallsGetConversationMessagesAsync()
    {
        _shortTermMemory.GetConversationMessagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Message>());

        await ConversationTools.MemoryGetConversation(_shortTermMemory, _conversationRepo, _isolationPolicy, "conv-1");

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

        var result = await ConversationTools.MemoryGetConversation(_shortTermMemory, _conversationRepo, _isolationPolicy, "conv-1");

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

        var result = await ConversationTools.MemoryGetConversation(_shortTermMemory, _conversationRepo, _isolationPolicy, "conv-1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task MemoryGetConversation_ScopedToOtherOwner_ReturnsEmpty_WithoutReadingMessages()
    {
        // cycle-5 R1: a scoped caller must not read another owner's conversation by guessing its id.
        _conversationRepo.GetByIdAsync("conv-1", Arg.Any<CancellationToken>())
            .Returns(new Conversation
            {
                ConversationId = "conv-1", SessionId = "ses-1", UserId = "alice",
                CreatedAtUtc = FixedTime, UpdatedAtUtc = FixedTime
            });

        var result = await ConversationTools.MemoryGetConversation(
            _shortTermMemory, _conversationRepo, _isolationPolicy, "conv-1", userId: "bob");

        JsonDocument.Parse(result).RootElement.GetArrayLength().Should().Be(0);
        await _shortTermMemory.DidNotReceive().GetConversationMessagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryGetConversation_ScopedToOwner_ReturnsMessages()
    {
        _conversationRepo.GetByIdAsync("conv-1", Arg.Any<CancellationToken>())
            .Returns(new Conversation
            {
                ConversationId = "conv-1", SessionId = "ses-1", UserId = "alice",
                CreatedAtUtc = FixedTime, UpdatedAtUtc = FixedTime
            });
        _shortTermMemory.GetConversationMessagesAsync("conv-1", Arg.Any<CancellationToken>())
            .Returns(new List<Message>
            {
                new() { MessageId = "m1", ConversationId = "conv-1", SessionId = "ses-1", Role = "user", Content = "hi", TimestampUtc = FixedTime }
            });

        var result = await ConversationTools.MemoryGetConversation(
            _shortTermMemory, _conversationRepo, _isolationPolicy, "conv-1", userId: "alice");

        JsonDocument.Parse(result).RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task MemoryGetConversation_ScopedToSharedConversation_IsAllowed()
    {
        // An un-attributed (UserId == null) conversation is shared/global — visible to any scoped owner.
        _conversationRepo.GetByIdAsync("conv-1", Arg.Any<CancellationToken>())
            .Returns(new Conversation
            {
                ConversationId = "conv-1", SessionId = "ses-1", UserId = null,
                CreatedAtUtc = FixedTime, UpdatedAtUtc = FixedTime
            });
        _shortTermMemory.GetConversationMessagesAsync("conv-1", Arg.Any<CancellationToken>())
            .Returns(new List<Message>
            {
                new() { MessageId = "m1", ConversationId = "conv-1", SessionId = "ses-1", Role = "user", Content = "hi", TimestampUtc = FixedTime }
            });

        var result = await ConversationTools.MemoryGetConversation(
            _shortTermMemory, _conversationRepo, _isolationPolicy, "conv-1", userId: "bob");

        JsonDocument.Parse(result).RootElement.GetArrayLength().Should().Be(1);
    }

    // ── memory_list_sessions ──

    [Fact]
    public async Task MemoryListSessions_CallsGetBySessionAsync()
    {
        _conversationRepo.GetBySessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Conversation>());

        await ConversationTools.MemoryListSessions(_conversationRepo, _options, _isolationPolicy, "ses-1");

        await _conversationRepo.Received(1).GetBySessionAsync("ses-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryListSessions_UsesDefaultSessionIdWhenNoneProvided()
    {
        _conversationRepo.GetBySessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Conversation>());

        await ConversationTools.MemoryListSessions(_conversationRepo, _options, _isolationPolicy);

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

        var result = await ConversationTools.MemoryListSessions(_conversationRepo, _options, _isolationPolicy, "ses-1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("conversationId").GetString().Should().Be("conv-1");
        doc.RootElement[0].GetProperty("sessionId").GetString().Should().Be("ses-1");
    }

    [Fact]
    public async Task MemoryListSessions_ScopedToOwner_ExcludesOtherOwnersAndKeepsShared()
    {
        // cycle-5 R1: a scoped caller sees only their own + un-attributed conversations, never another owner's.
        _conversationRepo.GetBySessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Conversation>
            {
                new() { ConversationId = "mine",   SessionId = "ses-1", UserId = "alice", CreatedAtUtc = FixedTime, UpdatedAtUtc = FixedTime },
                new() { ConversationId = "theirs", SessionId = "ses-1", UserId = "bob",   CreatedAtUtc = FixedTime, UpdatedAtUtc = FixedTime },
                new() { ConversationId = "shared", SessionId = "ses-1", UserId = null,    CreatedAtUtc = FixedTime, UpdatedAtUtc = FixedTime },
            });

        var result = await ConversationTools.MemoryListSessions(_conversationRepo, _options, _isolationPolicy, "ses-1", userId: "alice");

        var ids = JsonDocument.Parse(result).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("conversationId").GetString()).ToList();
        ids.Should().BeEquivalentTo(new[] { "mine", "shared" });
        ids.Should().NotContain("theirs");
    }

    // ── #100 Stage 2: StrictMultiTenant fails closed for both tools too ──

    private static IMemoryIsolationPolicy CreateStrictPolicy() =>
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions { Mode = MemoryIsolationMode.StrictMultiTenant }),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    [Fact]
    public async Task MemoryGetConversation_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var act = () => ConversationTools.MemoryGetConversation(
            _shortTermMemory, _conversationRepo, CreateStrictPolicy(), "conv-1");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _conversationRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _shortTermMemory.DidNotReceive().GetConversationMessagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryGetConversation_WithOwner_StrictMode_Succeeds()
    {
        _conversationRepo.GetByIdAsync("conv-1", Arg.Any<CancellationToken>())
            .Returns(new Conversation
            {
                ConversationId = "conv-1", SessionId = "ses-1", UserId = "alice",
                CreatedAtUtc = FixedTime, UpdatedAtUtc = FixedTime
            });
        _shortTermMemory.GetConversationMessagesAsync("conv-1", Arg.Any<CancellationToken>())
            .Returns(new List<Message>());

        var result = await ConversationTools.MemoryGetConversation(
            _shortTermMemory, _conversationRepo, CreateStrictPolicy(), "conv-1", userId: "alice");

        JsonDocument.Parse(result).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task MemoryListSessions_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var act = () => ConversationTools.MemoryListSessions(
            _conversationRepo, _options, CreateStrictPolicy(), "ses-1");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _conversationRepo.DidNotReceive().GetBySessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryListSessions_WithOwner_StrictMode_Succeeds()
    {
        _conversationRepo.GetBySessionAsync("ses-1", Arg.Any<CancellationToken>())
            .Returns(new List<Conversation>());

        var result = await ConversationTools.MemoryListSessions(
            _conversationRepo, _options, CreateStrictPolicy(), "ses-1", userId: "alice");

        JsonDocument.Parse(result).RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }
}
