using FluentAssertions;
using Microsoft.Agents.AI;
using AgentMemory.AgentFramework;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// Unit tests for <see cref="AgentSessionMemoryExtensions.GetMemoryIdentity"/> -- the single-source-of-truth
/// reader added for #90, replacing the inline <c>StateBag.TryGetValue</c> duplication previously in both
/// <see cref="Neo4jMemoryContextProvider"/> and <see cref="Neo4jChatHistoryProvider"/>.
/// </summary>
public sealed class AgentSessionMemoryExtensionsTests
{
    [Fact]
    public void GetMemoryIdentity_RoundTripsValuesWrittenByWithMemoryIdentity()
    {
        var session = new TestAgentSession().WithMemoryIdentity(
            userId: "alice", sessionId: "s1", conversationId: "c1", applicationId: "app1");

        var identity = session.GetMemoryIdentity();

        identity.UserId.Should().Be("alice");
        identity.SessionId.Should().Be("s1");
        identity.ConversationId.Should().Be("c1");
        identity.ApplicationId.Should().Be("app1");
    }

    [Fact]
    public void GetMemoryIdentity_NullSession_ReturnsDefault()
    {
        AgentSession? session = null;

        var identity = session.GetMemoryIdentity();

        identity.Should().Be(default(MemoryIdentity));
    }

    [Fact]
    public void GetMemoryIdentity_NoIdentityWritten_ReturnsAllNull()
    {
        var session = new TestAgentSession();

        var identity = session.GetMemoryIdentity();

        identity.UserId.Should().BeNull();
        identity.SessionId.Should().BeNull();
        identity.ConversationId.Should().BeNull();
        identity.ApplicationId.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetMemoryIdentity_BlankUserId_NormalizesToNull(string blank)
    {
        var session = new TestAgentSession();
        // WithMemoryIdentity itself skips null values but not blank ones, so write directly to exercise
        // GetMemoryIdentity's own blank-normalization independent of the writer.
        session.StateBag.SetValue(new AgentFrameworkOptions().DefaultUserIdKey, blank, System.Text.Json.JsonSerializerOptions.Default);

        var identity = session.GetMemoryIdentity();

        identity.UserId.Should().BeNull();
    }

    [Fact]
    public void GetMemoryIdentity_CustomKeyNames_AreRespected()
    {
        var options = new AgentFrameworkOptions
        {
            DefaultUserIdKey = "custom_user",
            DefaultSessionIdKey = "custom_session",
            DefaultConversationIdKey = "custom_conversation",
            DefaultApplicationIdKey = "custom_app",
        };
        var session = new TestAgentSession().WithMemoryIdentity(
            userId: "bob", sessionId: "s2", conversationId: "c2", applicationId: "app2", options: options);

        var identity = session.GetMemoryIdentity(options);

        identity.UserId.Should().Be("bob");
        identity.SessionId.Should().Be("s2");
        identity.ConversationId.Should().Be("c2");
        identity.ApplicationId.Should().Be("app2");
    }

    // AgentSession is abstract in Microsoft.Agents.AI.Abstractions; the concrete implementation
    // (ChatClientAgentSession) lives in the non-Abstractions Microsoft.Agents.AI package, which this test
    // project doesn't reference. A minimal concrete subclass is all GetMemoryIdentity/WithMemoryIdentity
    // need (they only touch StateBag).
    private sealed class TestAgentSession : AgentSession;
}
