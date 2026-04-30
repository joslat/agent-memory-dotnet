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
}
