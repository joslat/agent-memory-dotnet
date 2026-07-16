using FluentAssertions;
using Microsoft.Extensions.AI;
using AgentMemory.AgentFramework.Recall;

namespace AgentMemory.Tests.Unit.AgentFramework.Recall;

public sealed class ConfiguredAutomaticRecallPolicyTests
{
    private readonly ConfiguredAutomaticRecallPolicy _sut = new();

    private static AutomaticRecallContext Context(string text = "hi") => new()
    {
        Messages = new[] { new ChatMessage(ChatRole.User, text) },
        SessionId = "s1",
        ConversationId = "c1"
    };

    [Fact]
    public async Task DecideAsync_AlwaysRecalls()
    {
        var decision = await _sut.DecideAsync(Context());

        decision.ShouldRecall.Should().BeTrue();
    }

    [Fact]
    public async Task DecideAsync_UsesAllCategories_SoNoCategoryIsSilentlyDisabled()
    {
        // Categories=All means ResolveEffectiveOptions zeroes nothing -- whatever the host configured
        // (including reasoning traces / GraphRAG) reaches recall unchanged, the pre-#88 behavior.
        var decision = await _sut.DecideAsync(Context());

        decision.Categories.Should().Be(AutomaticRecallCategories.All);
    }

    [Fact]
    public async Task DecideAsync_DoesNotOverrideIntent()
    {
        var decision = await _sut.DecideAsync(Context());

        decision.Intent.Should().BeNull();
    }

    [Fact]
    public async Task DecideAsync_DoesNotOverrideRecallOptions()
    {
        var decision = await _sut.DecideAsync(Context());

        decision.RecallOptions.Should().BeNull();
    }

    [Fact]
    public async Task DecideAsync_ReturnsSameDecision_RegardlessOfMessageContent()
    {
        // The default policy is task-agnostic by design -- it never inspects the turn content.
        var greeting = await _sut.DecideAsync(Context("hello"));
        var task = await _sut.DecideAsync(Context("find a similar previous incident"));

        greeting.Should().Be(task);
    }
}
