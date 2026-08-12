using AgentMemory.Abstractions.Domain;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// Recalled chat history the host is already sending must not be sent twice (PLAN 2.5).
/// </summary>
/// <remarks>
/// <para>
/// The provider receives the full live thread and, until now, discarded it — so recall's
/// <c>RecentMessages</c> re-sent turns the model was already being given, and the host paid for both
/// copies.
/// </para>
/// <para>
/// <b>The role trap is the interesting part.</b> <c>RecalledMessageRoleGate</c> rewrites a recalled
/// message's role — privileged down to <c>user</c> below the trust threshold — while leaving its
/// content identical. A role-keyed fingerprint therefore matches nothing on precisely the hosts that
/// hardened <c>MinimumTrustForSystemRole</c>: the feature would work everywhere except the
/// security-conscious configuration, and look correct in every test that did not set that option.
/// </para>
/// </remarks>
public sealed class HistoryThreadDedupTests
{
    private static Message Recalled(string content, string role = "user") => new()
    {
        MessageId = $"m-{content.GetHashCode():X}",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = role,
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    private static MemoryContext Context(params Message[] recent) => new()
    {
        SessionId = "s-1",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        RecentMessages = new MemoryContextSection<Message> { Items = recent },
    };

    private static IReadOnlyList<ChatMessage> Map(
        MemoryContext context,
        IReadOnlyList<ChatMessage>? liveThread,
        int chatBudget = 10,
        MemoryTrustLevel systemRoleThreshold = MemoryTrustLevel.Untrusted) =>
        MafTypeMapper.ToContextMessages(
            context,
            new ContextFormatOptions
            {
                MaxChatHistoryMessages = chatBudget,
                MinimumTrustForSystemRole = systemRoleThreshold,
            },
            admissionPolicy: null,
            logger: null,
            liveThread: liveThread);

    [Fact]
    public void ARecalledMessageAlreadyInTheLiveThreadIsDropped()
    {
        var mapped = Map(
            Context(Recalled("what is my deploy command"), Recalled("an older turn")),
            [new ChatMessage(ChatRole.User, "what is my deploy command")]);

        mapped.Select(m => m.Text).Should().NotContain("what is my deploy command");
        mapped.Select(m => m.Text).Should().Contain(t => t.Contains("an older turn"));
    }

    [Fact]
    public void MatchingIsOnContentEvenWhenTheRoleWasRewritten()
    {
        // THE case. The live thread carries an assistant turn; recall returns the same content, and
        // the role gate rewrites it to user because its trust is below the threshold. Content is
        // identical, roles are not -- a role-keyed fingerprint would miss it.
        var recalled = Recalled("I recommended the 14:05 train", role: "assistant");

        var mapped = Map(
            Context(recalled),
            [new ChatMessage(ChatRole.Assistant, "I recommended the 14:05 train")],
            systemRoleThreshold: MemoryTrustLevel.ApplicationTrusted);

        mapped.Select(m => m.Text).Should().NotContain(t => t.Contains("14:05 train"));
    }

    [Fact]
    public void TheDedupHappensBeforeTheChatBudget()
    {
        // The quality half, not just the cost half. With a budget of 2 and the two newest turns
        // already in the thread, filtering first lets the budget carry two OLDER messages the model
        // has not seen; filtering afterwards would have spent both slots on duplicates and delivered
        // nothing.
        var mapped = Map(
            Context(Recalled("newest"), Recalled("second"), Recalled("third"), Recalled("fourth")),
            [new ChatMessage(ChatRole.User, "newest"), new ChatMessage(ChatRole.User, "second")],
            chatBudget: 2);

        var texts = mapped.Select(m => m.Text).ToList();
        texts.Should().Contain(t => t.Contains("third"));
        texts.Should().Contain(t => t.Contains("fourth"));
        texts.Should().NotContain(t => t.Contains("newest"));
    }

    [Fact]
    public void WhitespaceAndCasingDoNotDefeatTheMatch()
    {
        // The live thread and the stored copy travel different paths -- one through the host's own
        // formatting, one through persistence and back. A trailing newline is not a different message.
        var mapped = Map(
            Context(Recalled("Deploy   the service\n")),
            [new ChatMessage(ChatRole.User, "deploy the service")]);

        mapped.Select(m => m.Text).Should().NotContain(t => t.Contains("Deploy"));
    }

    [Fact]
    public void ANonMatchingLiveThreadChangesNothing()
    {
        var mapped = Map(
            Context(Recalled("stored turn")),
            [new ChatMessage(ChatRole.User, "a completely different question")]);

        mapped.Select(m => m.Text).Should().Contain(t => t.Contains("stored turn"));
    }

    [Fact]
    public void NoLiveThreadLeavesTheOutputByteIdentical()
    {
        // The off switch and the byte-identical guarantee: passing null must reproduce exactly the
        // pre-2.5 behaviour, which is what every recorded measurement was taken under.
        var context = Context(Recalled("stored turn"), Recalled("another"));

        var withoutThread = Map(context, liveThread: null);
        var withEmptyThread = Map(context, liveThread: []);

        withoutThread.Select(m => m.Text).Should().Equal(withEmptyThread.Select(m => m.Text));
        // Both recalled turns survive. Counted by content rather than by list length, because the
        // mapper also emits the security prefix -- asserting a raw count would break whenever that
        // preamble changed, for a reason unrelated to dedup.
        withoutThread.Select(m => m.Text).Should()
            .Contain(t => t.Contains("stored turn"))
            .And.Contain(t => t.Contains("another"));
    }

    [Fact]
    public void AnEmptyOrBlankLiveMessageMatchesNothing()
    {
        // A blank entry normalises to the empty string; if that were a valid key it would match every
        // recalled message whose content was also blank, and worse, invite an empty-vs-empty match
        // that silently drops content.
        var mapped = Map(
            Context(Recalled("stored turn")),
            [new ChatMessage(ChatRole.User, "   "), new ChatMessage(ChatRole.User, "")]);

        mapped.Select(m => m.Text).Should().Contain(t => t.Contains("stored turn"));
    }

    [Fact]
    public void TheNormalizerIsCultureIndependent()
    {
        // ToUpperInvariant, not ToUpper: a Turkish locale folds 'i' to 'İ', so a culture-sensitive
        // normaliser would make dedup depend on the host's locale.
        MafTypeMapper.NormalizeForDedup("Istanbul  file")
            .Should().Be(MafTypeMapper.NormalizeForDedup("ISTANBUL FILE"));
    }

    [Fact]
    public void DedupIsOnByDefault()
    {
        // Sending the model two copies of the same turn has no upside, and the comparison is a hash
        // set over a thread the provider already holds.
        new AgentFrameworkOptions().DeduplicateRecalledHistory.Should().BeTrue();
    }
}
