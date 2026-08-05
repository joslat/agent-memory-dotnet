using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalPreparationWatchdogTests
{
    [Fact]
    public async Task RunAsync_CompletesWhenExpectedProviderProgressFinishes()
    {
        var provider = Substitute.For<IChatClient>();
        provider.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "{}")));
        using var meter = new LongMemEvalChatCallMeter(provider);

        var result = await LongMemEvalPreparationWatchdog.RunAsync(
            async cancellationToken =>
            {
                _ = await meter.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, "bounded test")],
                    cancellationToken: cancellationToken);
                return 42;
            },
            meter,
            expectedProviderCalls: 1,
            overallTimeout: TimeSpan.FromSeconds(1),
            noProviderProgressTimeout: TimeSpan.FromMilliseconds(100),
            phase: "test",
            output: TextWriter.Null);

        result.Should().Be(42);
        meter.Snapshot().CompletedCalls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NoProviderProgressFailsWithBoundedDiagnostics()
    {
        var provider = Substitute.For<IChatClient>();
        using var meter = new LongMemEvalChatCallMeter(provider);

        var act = () => LongMemEvalPreparationWatchdog.RunAsync(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            },
            meter,
            expectedProviderCalls: 1,
            overallTimeout: TimeSpan.FromSeconds(1),
            noProviderProgressTimeout: TimeSpan.FromMilliseconds(50),
            phase: "test",
            output: TextWriter.Null);

        var exception = await act.Should().ThrowAsync<TimeoutException>();
        exception.Which.Message.Should().Contain("no-provider-progress");
        exception.Which.Message.Should().Contain("started/completed=0/0");
        exception.Which.Message.Should().Contain("first_failure_type=none");
        exception.Which.Message.Should().NotContain("bounded test");
    }

    [Fact]
    public async Task RunAsync_RejectsNoProgressWindowBeyondOverallTimeout()
    {
        using var meter = new LongMemEvalChatCallMeter(Substitute.For<IChatClient>());
        var act = () => LongMemEvalPreparationWatchdog.RunAsync(
            _ => Task.FromResult(0), meter, 1,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20),
            "test", TextWriter.Null);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
