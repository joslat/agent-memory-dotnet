using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class BoundedWorkSchedulerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task RunAsync_IsBoundedAndReturnsResultsInInputOrder(int workers)
    {
        const int count = 10;
        var allWorkersAdmitted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;

        var work = Enumerable.Range(0, count)
            .Select(index => (Func<CancellationToken, Task<int>>)(async cancellationToken =>
            {
                if (Interlocked.Increment(ref admitted) == workers)
                    allWorkersAdmitted.TrySetResult();
                await allWorkersAdmitted.Task.WaitAsync(cancellationToken);
                return index;
            }))
            .ToArray();

        var result = await BoundedWorkScheduler.RunAsync(
            work,
            workers,
            CancellationToken.None);

        result.MaxConcurrency.Should().Be(workers);
        result.Results.Should().Equal(Enumerable.Range(0, count));
    }

    [Fact]
    public async Task RunAsync_CancellationStopsAdmission()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = 0;
        Func<CancellationToken, Task<int>> work = async token =>
        {
            Interlocked.Increment(ref entered);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        };

        var running = BoundedWorkScheduler.RunAsync(
            Enumerable.Repeat(work, 10).ToArray(), 2, cancellation.Token);
        await Task.Delay(20);
        await cancellation.CancelAsync();

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
        entered.Should().BeLessThanOrEqualTo(2);
    }
}
