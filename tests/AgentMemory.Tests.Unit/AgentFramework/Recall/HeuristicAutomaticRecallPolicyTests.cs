using FluentAssertions;
using Microsoft.Extensions.AI;
using AgentMemory.Abstractions.Options;
using AgentMemory.AgentFramework.Recall;

namespace AgentMemory.Tests.Unit.AgentFramework.Recall;

public sealed class HeuristicAutomaticRecallPolicyTests
{
    private readonly HeuristicAutomaticRecallPolicy _sut = new();

    private static AutomaticRecallContext Context(params string[] userTexts) => new()
    {
        Messages = userTexts.Select(t => new ChatMessage(ChatRole.User, t)).ToList(),
        SessionId = "s1",
        ConversationId = "c1"
    };

    // ── Skip: empty / greeting-only ─────────────────────────────────────────

    [Theory]
    [InlineData("hi")]
    [InlineData("Hello")]
    [InlineData("thanks!")]
    [InlineData("ok, got it.")]
    [InlineData("sounds good")]
    public async Task DecideAsync_GreetingOrAcknowledgementOnly_SkipsRecall(string text)
    {
        var decision = await _sut.DecideAsync(Context(text));

        decision.ShouldRecall.Should().BeFalse();
    }

    [Fact]
    public async Task DecideAsync_NoMessages_SkipsRecall()
    {
        var decision = await _sut.DecideAsync(Context());

        decision.ShouldRecall.Should().BeFalse();
    }

    [Fact]
    public async Task DecideAsync_WhitespaceOnlyMessage_SkipsRecall()
    {
        var decision = await _sut.DecideAsync(Context("   "));

        decision.ShouldRecall.Should().BeFalse();
    }

    // ── Default: an ordinary substantive question ───────────────────────────

    [Fact]
    public async Task DecideAsync_OrdinaryQuestion_RecallsWithDefaultCategoriesPlusGraphRag()
    {
        var decision = await _sut.DecideAsync(Context("What seat do I normally prefer?"));

        decision.ShouldRecall.Should().BeTrue();
        decision.Categories.Should().Be(AutomaticRecallCategories.Default | AutomaticRecallCategories.GraphRag);
        decision.Intent.Should().BeNull();
    }

    // ── #88 review fix: GraphRAG must never be excluded by this policy ──────
    // AutomaticRecallCategories.Default (the enum's own baseline) deliberately excludes GraphRag, but this
    // policy has no rule about GraphRAG at all -- it must never silently disable a host's independently
    // configured EnableGraphRag/MaxGraphRagItems/BlendMode, in any branch.

    [Theory]
    [InlineData("What seat do I normally prefer?")]
    [InlineData("What is the latest decision about Project Atlas?")]
    [InlineData("Find a similar previous incident")]
    [InlineData("Help me debug this error")]
    public async Task DecideAsync_NeverExcludesGraphRagCategory(string text)
    {
        var decision = await _sut.DecideAsync(Context(text));

        decision.Categories.HasFlag(AutomaticRecallCategories.GraphRag).Should().BeTrue();
    }

    // ── Recency-oriented phrasing → RankingIntent.Latest ────────────────────

    [Theory]
    [InlineData("What is the latest decision about Project Atlas?")]
    [InlineData("What's the current status?")]
    [InlineData("What is happening right now?")]
    public async Task DecideAsync_RecencyOrientedPhrasing_UsesLatestIntent(string text)
    {
        var decision = await _sut.DecideAsync(Context(text));

        decision.ShouldRecall.Should().BeTrue();
        decision.Intent.Should().Be(RankingIntent.Latest);
    }

    // ── Precedent-oriented phrasing → RankingIntent.Analog + ReasoningTraces ─

    [Theory]
    [InlineData("Find a similar previous incident")]
    [InlineData("How did we solve this before?")]
    [InlineData("Is there a precedent for this?")]
    public async Task DecideAsync_PrecedentOrientedPhrasing_UsesAnalogIntentAndIncludesTraces(string text)
    {
        var decision = await _sut.DecideAsync(Context(text));

        decision.ShouldRecall.Should().BeTrue();
        decision.Intent.Should().Be(RankingIntent.Analog);
        decision.Categories.HasFlag(AutomaticRecallCategories.ReasoningTraces).Should().BeTrue();
    }

    // ── Task/troubleshooting phrasing → ReasoningTraces, no intent override ──

    [Theory]
    [InlineData("Help me debug this error")]
    [InlineData("Walk me through the workflow")]
    [InlineData("What's the root cause of this bug?")]
    public async Task DecideAsync_TaskOrientedPhrasing_IncludesReasoningTraces(string text)
    {
        var decision = await _sut.DecideAsync(Context(text));

        decision.ShouldRecall.Should().BeTrue();
        decision.Categories.HasFlag(AutomaticRecallCategories.ReasoningTraces).Should().BeTrue();
    }

    [Fact]
    public async Task DecideAsync_NeverSetsExplicitRecallOptionsOverride()
    {
        // The heuristic policy only ever expresses itself via Categories/Intent, never a full override.
        var decision = await _sut.DecideAsync(Context("Find a similar previous incident"));

        decision.RecallOptions.Should().BeNull();
    }

    [Fact]
    public async Task DecideAsync_MultipleUserMessages_JoinsThemForKeywordMatching()
    {
        var decision = await _sut.DecideAsync(Context("Tell me about the project.", "What's the latest update?"));

        decision.Intent.Should().Be(RankingIntent.Latest);
    }

    // ── #88 review fix: greeting detection is a linear-time tokenizer, not a regex ──
    // A previous regex-based implementation (a single repeating group with nested quantifiers) exhibited
    // catastrophic backtracking -- verified experimentally to take multiple seconds on adversarial input.
    // The tokenizer has no such failure mode; this test guards against a regex-based regression.

    [Fact]
    public async Task DecideAsync_ManyRepeatedGreetingFragments_CompletesQuickly()
    {
        var pathologicalInput = string.Concat(Enumerable.Repeat("hi ", 200)) + "X";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decision = await _sut.DecideAsync(Context(pathologicalInput));
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500);
        // Trailing "X" is not a greeting/ack word, so this must NOT be treated as greeting-only.
        decision.ShouldRecall.Should().BeTrue();
    }

    [Fact]
    public async Task DecideAsync_LongChainOfGenuineGreetingFragments_StillSkipsRecall()
    {
        var allGreetings = string.Concat(Enumerable.Repeat("ok, got it. thanks! ", 50));

        var decision = await _sut.DecideAsync(Context(allGreetings));

        decision.ShouldRecall.Should().BeFalse();
    }

    [Fact]
    public async Task DecideAsync_GreetingFollowedByRealQuestion_DoesNotSkipRecall()
    {
        // A greeting-only PREFIX must not cause the whole turn to be skipped once real content follows.
        var decision = await _sut.DecideAsync(Context("Hi, what do I usually order for lunch?"));

        decision.ShouldRecall.Should().BeTrue();
    }
}
