using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

/// <summary>The chat-model gate (R-chat-judge): one call, the gate's question, a whole decision or none.</summary>
public sealed class ChatModelMemoryGateTests
{
    private sealed class ScriptedChat(string reply, string? modelId = "glm") : IChatClient
    {
        public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((messages.ToList(), options));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { ModelId = modelId });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static readonly MemoryGateRequest Request = new(
        "where should I take Ana for dinner?",
        [new MemoryGateTurn("user", "Ana visits on Friday"), new MemoryGateTurn("assistant", "Lovely!")],
        new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero),
        [
            new MemoryGateCandidate("semantic|F1", "semantic", "Ana | is | vegetarian (since 3 Jan 2020)"),
            new MemoryGateCandidate("preference|P1", "preference", "Dislikes coriander"),
            new MemoryGateCandidate("semantic|F2", "semantic", "Marta | runs | 10 km"),
        ]);

    [Fact]
    public async Task OneCallAnswersEveryMemoryUnderItsOwnKey()
    {
        var chat = new ScriptedChat("Sure: {\"m1\": 95, \"m2\": \"60\", \"m3\": 0}");
        var decision = await new ChatModelMemoryGate(chat).DecideAsync(Request);
        chat.Requests.Should().ContainSingle();
        decision.Probabilities.Should().Equal(new Dictionary<string, double> { ["semantic|F1"] = 0.95, ["preference|P1"] = 0.60, ["semantic|F2"] = 0 });
        decision.AnsweredBy.Should().Be("chat:glm");
        chat.Requests[0].Options!.Temperature.Should().Be(0);
    }

    [Fact]
    public void ThePromptAsksTheGatesQuestionWithTheDateTheTurnAndEachTypesMeaning()
    {
        var messages = ChatModelMemoryGate.Messages(Request, ["m1", "m2", "m3"]);
        messages[0].Text.Should().Contain(GatePrompt.Question.True).And.Contain(GatePrompt.Question.False)
            .And.Contain("Should memory mN be put in front of the assistant before it replies to the user's turn?");
        var user = messages[1].Text;
        user.Should().Contain("Today: Saturday 3 October 2026").And.Contain("user: Ana visits on Friday")
            .And.Contain("The user's turn: where should I take Ana for dinner?")
            .And.Contain("Type semantic: " + GatePrompt.Meanings["semantic"]).And.Contain("Type preference: " + GatePrompt.Meanings["preference"])
            .And.Contain("m1: Ana | is | vegetarian").And.Contain("m3: Marta | runs | 10 km");
        user.IndexOf("Type semantic", StringComparison.Ordinal).Should().BeLessThan(user.IndexOf("Type preference", StringComparison.Ordinal));
        user.Split("Type semantic").Should().HaveCount(2, "a type's memories are listed together");
    }

    [Fact]
    public void ACallersOwnMeaningsReplaceTheDefaultWording()
    {
        var messages = ChatModelMemoryGate.Messages(Request, ["m1", "m2", "m3"],
            new Dictionary<string, string> { ["semantic"] = "facts about anyone" });
        messages[1].Text.Should().Contain("Type semantic: facts about anyone").And.Contain("Type preference: preference");
    }

    [Theory]
    [InlineData("{\"m1\": 95, \"m2\": 60}")]
    [InlineData("{\"m1\": 95, \"m2\": 60, \"m3\": 101}")]
    [InlineData("{\"m1\": 95, \"m2\": -1, \"m3\": 3}")]
    [InlineData("{\"m1\": \"yes\", \"m2\": 60, \"m3\": 3}")]
    [InlineData("no JSON here")]
    [InlineData("{\"m1\": 95, \"m2\": 60, \"m3\": }")]
    public async Task AnythingButAWholeDecisionIsRefusedSoTheCallerTakesTheFloor(string reply)
    {
        var act = () => new ChatModelMemoryGate(new ScriptedChat(reply)).DecideAsync(Request);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NoCandidatesMakesNoCall()
    {
        var chat = new ScriptedChat("{}");
        var decision = await new ChatModelMemoryGate(chat).DecideAsync(Request with { Candidates = [] });
        decision.Probabilities.Should().BeEmpty();
        chat.Requests.Should().BeEmpty();
    }
}
