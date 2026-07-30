using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalCallDetailSafetyTests
{
    [Fact]
    public async Task AllCallDetailsAreBoundedAndContainNoPromptOrResponseText()
    {
        var provider = Substitute.For<IChatClient>();
        provider.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "sensitive response")));
        using var meter = new LongMemEvalChatCallMeter(provider);

        for (var index = 0; index < 65; index++)
        {
            await meter.GetResponseAsync(
            [
                new ChatMessage(
                    ChatRole.System,
                    "You are an entity extraction assistant. sensitive prompt")
            ]);
        }

        var snapshot = meter.Snapshot();
        snapshot.Calls.Should().Be(65);
        snapshot.CallDetails.Should().HaveCount(64);
        snapshot.CallDetails.Select(detail => detail.CallOrdinal)
            .Should().Equal(Enumerable.Range(2, 64).Select(value => (long)value));
        snapshot.CallDetails.Should().OnlyContain(detail =>
            detail.Purpose == "entity" &&
            detail.ExceptionType == null &&
            detail.ProviderStatus == null);
        snapshot.DroppedCallDetails.Should().Be(1);
        snapshot.ToString().Should().NotContain("sensitive");
    }
}
