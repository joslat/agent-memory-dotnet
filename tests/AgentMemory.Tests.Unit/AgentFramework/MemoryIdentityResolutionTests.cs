using AgentMemory.AgentFramework;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// RVW-REST-HOSTS (the review's hosts-2 and hosts-4): the ids both Agent Framework providers read and write under.
/// </summary>
public sealed class MemoryIdentityResolutionTests
{
    private static MemoryIdentity Identity(string? user = null, string? session = null, string? conversation = null) =>
        new(user, session, conversation, null);

    [Fact]
    public void Users_of_one_agent_without_a_session_id_get_sessions_of_their_own()
    {
        var alice = MemoryIdentityResolution.Resolve(Identity(user: "alice"), "agent-1", ambientOwner: null);
        var bob = MemoryIdentityResolution.Resolve(Identity(user: "bob"), "agent-1", ambientOwner: null);

        alice.SessionId.Should().Be("agent-1:alice");
        bob.SessionId.Should().Be("agent-1:bob");
        alice.ConversationId.Should().Be(alice.SessionId);
    }

    [Fact]
    public void The_ambient_owner_stands_in_when_the_session_names_none()
    {
        var resolved = MemoryIdentityResolution.Resolve(Identity(), "agent-1", ambientOwner: "alice");

        resolved.UserId.Should().Be("alice");
        resolved.SessionId.Should().Be("agent-1:alice");
    }

    [Fact]
    public void What_the_session_names_wins()
    {
        var resolved = MemoryIdentityResolution.Resolve(Identity(user: "bob", session: "s1", conversation: "c1"), "agent-1", ambientOwner: "alice");

        resolved.Should().Be(("s1", "c1", "bob", (string?)null));
    }

    [Fact]
    public void Without_an_owner_the_agents_id_is_the_session_as_before()
    {
        MemoryIdentityResolution.Resolve(Identity(), "agent-1", ambientOwner: null).SessionId.Should().Be("agent-1");
        MemoryIdentityResolution.Resolve(Identity(), agentId: null, ambientOwner: null).SessionId.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
