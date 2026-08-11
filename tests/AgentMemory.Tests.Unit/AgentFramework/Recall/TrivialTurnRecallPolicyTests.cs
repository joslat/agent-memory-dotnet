using AgentMemory.AgentFramework.Recall;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework.Recall;

/// <summary>
/// The shipped default policy: full recall on a real turn, recent messages only on a trivial one.
/// </summary>
public sealed class TrivialTurnRecallPolicyTests
{
    private readonly TrivialTurnRecallPolicy _sut = new();

    private static AutomaticRecallContext Context(string text) => new()
    {
        Messages = new[] { new ChatMessage(ChatRole.User, text) },
        SessionId = "s1",
        ConversationId = "c1",
    };

    [Theory]
    [InlineData("hi")]
    [InlineData("ok, got it.")]
    [InlineData("thanks!")]
    [InlineData("sounds good")]
    [InlineData("")]
    public async Task ATrivialTurnNarrowsToRecentMessagesOnly(string text)
    {
        var decision = await _sut.DecideAsync(Context(text));

        decision.Categories.Should().Be(AutomaticRecallCategories.RecentMessages);
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("ok, go ahead")]
    public async Task ATrivialTurnStillRecalls_SoTheConversationIsNotLost(string text)
    {
        // The load-bearing distinction from a full skip. "ok, go ahead" is exactly the turn where the
        // agent most needs the conversation so far to know what it is agreeing to, and a skip would
        // drop RecentMessages along with everything else.
        var decision = await _sut.DecideAsync(Context(text));

        decision.ShouldRecall.Should().BeTrue();
        decision.Categories.Should().HaveFlag(AutomaticRecallCategories.RecentMessages);
    }

    [Fact]
    public async Task ATrivialTurnExcludesEveryVectorCategory_SoTheEmbeddingIsElided()
    {
        // The saving depends entirely on no embedding-consuming category surviving; the assembler and
        // the provider both gate the embedding on exactly these five.
        var decision = await _sut.DecideAsync(Context("ok"));

        decision.Categories.Should().NotHaveFlag(AutomaticRecallCategories.RelevantMessages);
        decision.Categories.Should().NotHaveFlag(AutomaticRecallCategories.Entities);
        decision.Categories.Should().NotHaveFlag(AutomaticRecallCategories.Facts);
        decision.Categories.Should().NotHaveFlag(AutomaticRecallCategories.Preferences);
        decision.Categories.Should().NotHaveFlag(AutomaticRecallCategories.ReasoningTraces);
    }

    [Theory]
    [InlineData("what did we decide about pricing?")]
    [InlineData("ok, so what was the deployment step again?")]
    [InlineData("thanks — can you remind me who owns billing?")]
    public async Task ARealTurnIsByteIdenticalToTheOldDefault(string text)
    {
        // Anything carrying a question must be untouched: same ShouldRecall, same Categories.All, same
        // null intent as ConfiguredAutomaticRecallPolicy. A greeting PREFIX must not trivialise a turn.
        var decision = await _sut.DecideAsync(Context(text));

        decision.ShouldRecall.Should().BeTrue();
        decision.Categories.Should().Be(AutomaticRecallCategories.All);
        decision.Intent.Should().BeNull();
        decision.RecallOptions.Should().BeNull();
        decision.RequiresRelationCompleteness.Should().BeFalse();
    }

    [Fact]
    public async Task ARealTurnMatchesConfiguredAutomaticRecallPolicyExactly()
    {
        // Stated as an equivalence rather than a list of fields, so a future property added to
        // AutomaticRecallDecision cannot drift between the old default and the new one unnoticed.
        var ctx = Context("what did we decide about pricing?");

        var mine = await _sut.DecideAsync(ctx);
        var previousDefault = await new ConfiguredAutomaticRecallPolicy().DecideAsync(ctx);

        mine.Should().BeEquivalentTo(previousDefault);
    }
}
