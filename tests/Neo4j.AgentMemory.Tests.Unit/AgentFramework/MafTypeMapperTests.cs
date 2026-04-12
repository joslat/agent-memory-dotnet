using FluentAssertions;
using Microsoft.Extensions.AI;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using Neo4j.AgentMemory.AgentFramework.Mapping;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.AgentFramework;

public sealed class MafTypeMapperTests
{
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    public MafTypeMapperTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2025, 1, 28, 12, 0, 0, TimeSpan.Zero));
        _idGen.GenerateId().Returns("msg-001");
    }

    // ── ToInternalMessage ──────────────────────────────────────────────────

    [Fact]
    public void ToInternalMessage_UserMessage_MapsCorrectly()
    {
        var chat = new ChatMessage(ChatRole.User, "Hello!");

        var result = MafTypeMapper.ToInternalMessage(chat, "s1", "c1", _clock, _idGen);

        result.MessageId.Should().Be("msg-001");
        result.SessionId.Should().Be("s1");
        result.ConversationId.Should().Be("c1");
        result.Role.Should().Be("user");
        result.Content.Should().Be("Hello!");
        result.TimestampUtc.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public void ToInternalMessage_AssistantMessage_MapsRole()
    {
        var chat = new ChatMessage(ChatRole.Assistant, "Hi there.");

        var result = MafTypeMapper.ToInternalMessage(chat, "s1", "c1", _clock, _idGen);

        result.Role.Should().Be("assistant");
    }

    [Fact]
    public void ToInternalMessage_SystemMessage_MapsRole()
    {
        var chat = new ChatMessage(ChatRole.System, "You are helpful.");

        var result = MafTypeMapper.ToInternalMessage(chat, "s1", "c1", _clock, _idGen);

        result.Role.Should().Be("system");
    }

    [Fact]
    public void ToInternalMessage_NullText_MapsToEmptyContent()
    {
        var chat = new ChatMessage { Role = ChatRole.User };

        var result = MafTypeMapper.ToInternalMessage(chat, "s1", "c1", _clock, _idGen);

        result.Content.Should().Be(string.Empty);
    }

    // ── ToChatMessage ──────────────────────────────────────────────────────

    [Fact]
    public void ToChatMessage_UserRole_MapsCorrectly()
    {
        var msg = new Message
        {
            MessageId = "1", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "Hello", TimestampUtc = DateTimeOffset.UtcNow
        };

        var result = MafTypeMapper.ToChatMessage(msg);

        result.Role.Should().Be(ChatRole.User);
        result.Text.Should().Be("Hello");
    }

    [Fact]
    public void ToChatMessage_AssistantRole_MapsCorrectly()
    {
        var msg = new Message
        {
            MessageId = "1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Hi", TimestampUtc = DateTimeOffset.UtcNow
        };

        var result = MafTypeMapper.ToChatMessage(msg);

        result.Role.Should().Be(ChatRole.Assistant);
    }

    [Fact]
    public void ToChatMessage_SystemRole_MapsCorrectly()
    {
        var msg = new Message
        {
            MessageId = "1", SessionId = "s1", ConversationId = "c1",
            Role = "system", Content = "Instruction", TimestampUtc = DateTimeOffset.UtcNow
        };

        var result = MafTypeMapper.ToChatMessage(msg);

        result.Role.Should().Be(ChatRole.System);
    }

    [Fact]
    public void ToChatMessage_UnknownRole_MapsToCustomChatRole()
    {
        var msg = new Message
        {
            MessageId = "1", SessionId = "s1", ConversationId = "c1",
            Role = "custom", Content = "data", TimestampUtc = DateTimeOffset.UtcNow
        };

        var result = MafTypeMapper.ToChatMessage(msg);

        result.Role.Value.Should().Be("custom");
    }

    // ── ToContextMessages ──────────────────────────────────────────────────

    [Fact]
    public void ToContextMessages_EmptyContext_ReturnsOnlyPrefix()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UtcNow
        };

        var result = MafTypeMapper.ToContextMessages(context);

        result.Should().HaveCount(1);
        result[0].Role.Should().Be(ChatRole.System);
        result[0].Text.Should().Contain("context from memory");
    }

    [Fact]
    public void ToContextMessages_WithMessages_IncludesMessages()
    {
        var msg = new Message
        {
            MessageId = "1", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "Hi", TimestampUtc = DateTimeOffset.UtcNow
        };
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UtcNow,
            RecentMessages = new MemoryContextSection<Message> { Items = [msg] }
        };

        var result = MafTypeMapper.ToContextMessages(context);

        result.Should().HaveCount(2); // prefix + message
        result[1].Text.Should().Be("Hi");
    }

    [Fact]
    public void ToContextMessages_WithEntities_IncludesEntityMessage()
    {
        var entity = new Entity
        {
            EntityId = "e1", Name = "Alice", Type = "Person",
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UtcNow,
            RelevantEntities = new MemoryContextSection<Entity> { Items = [entity] }
        };

        var result = MafTypeMapper.ToContextMessages(context, new ContextFormatOptions { IncludeEntities = true });

        result.Any(m => m.Text != null && m.Text.Contains("Alice")).Should().BeTrue();
    }

    [Fact]
    public void ToContextMessages_EntitiesDisabled_ExcludesEntityMessage()
    {
        var entity = new Entity
        {
            EntityId = "e1", Name = "Alice", Type = "Person",
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UtcNow,
            RelevantEntities = new MemoryContextSection<Entity> { Items = [entity] }
        };

        var result = MafTypeMapper.ToContextMessages(context, new ContextFormatOptions { IncludeEntities = false });

        result.Any(m => m.Text != null && m.Text.Contains("Alice")).Should().BeFalse();
    }

    [Fact]
    public void ToContextMessages_RespectsMaxContextMessages()
    {
        var messages = Enumerable.Range(1, 20)
            .Select(i => new Message
            {
                MessageId = $"m{i}", SessionId = "s1", ConversationId = "c1",
                Role = "user", Content = $"msg {i}", TimestampUtc = DateTimeOffset.UtcNow
            })
            .ToList();

        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UtcNow,
            RecentMessages = new MemoryContextSection<Message> { Items = messages }
        };

        var result = MafTypeMapper.ToContextMessages(context, new ContextFormatOptions { MaxContextMessages = 5 });

        result.Should().HaveCount(5);
    }

    // ── Role mapping helpers ───────────────────────────────────────────────

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    [InlineData("system")]
    [InlineData("tool")]
    public void ToInternalRole_RoundTrips(string role)
    {
        var chatRole = MafTypeMapper.ToMafRole(role);
        var back = MafTypeMapper.ToInternalRole(chatRole);
        back.Should().Be(role);
    }
}
