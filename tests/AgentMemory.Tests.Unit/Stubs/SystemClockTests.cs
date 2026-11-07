using AgentMemory.Abstractions.Services;
using NSubstitute;
using FluentAssertions;
using AgentMemory.Core.Stubs;

namespace AgentMemory.Tests.Unit.Stubs;

public class SystemClockTests
{
    [Fact]
    public void UtcNow_ReturnsCurrentTime()
    {
        var clock = new SystemClock();
        var before = DateTimeOffset.UtcNow;
        var result = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;
        result.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void UtcNow_IsUtc()
    {
        var clock = new SystemClock();
        clock.UtcNow.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void UtcNow_Advances()
    {
        var clock = new SystemClock();
        var first = clock.UtcNow;
        Thread.Sleep(5);
        var second = clock.UtcNow;
        second.Should().BeAfter(first);
    }

    [Fact]
    public void ReadsTheTimeProviderItIsGiven()
    {
        var at = new DateTimeOffset(2026, 12, 11, 9, 0, 0, TimeSpan.Zero);
        var time = NSubstitute.Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(at);

        new SystemClock(time).UtcNow.Should().Be(at);
        new SystemClock(time).ToTimeProvider().Should().BeSameAs(time, "a clock backed by a TimeProvider hands back that provider");
    }
}
