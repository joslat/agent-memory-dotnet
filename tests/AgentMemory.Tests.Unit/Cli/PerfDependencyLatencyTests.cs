using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class PerfDependencyLatencyTests
{
    [Fact]
    public void ResolveEmbeddingDelay_WithoutScenarioPreset_UsesRunWideDelay()
    {
        var sut = new PerfDependencyLatency();
        var runWide = TimeSpan.FromMilliseconds(120);

        sut.Current.Should().BeNull();
        sut.ResolveEmbeddingDelay(runWide).Should().Be(runWide);
    }

    [Fact]
    public async Task Push_FlowsAcrossAsyncWork_AndRestoresPreviousPreset()
    {
        var sut = new PerfDependencyLatency();
        var outer = PerfDependencyLatencyPreset.Degraded;
        var inner = new PerfDependencyLatencyPreset(
            "inner",
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20));

        using (sut.Push(outer))
        {
            sut.Current.Should().BeSameAs(outer);
            sut.ResolveEmbeddingDelay(TimeSpan.Zero).Should().Be(outer.EmbeddingDelay);

            var flowed = await Task.Run(() => sut.Current);
            flowed.Should().BeSameAs(outer);

            using (sut.Push(inner))
            {
                sut.Current.Should().BeSameAs(inner);
                sut.ResolveEmbeddingDelay(TimeSpan.Zero).Should().Be(inner.EmbeddingDelay);
            }

            sut.Current.Should().BeSameAs(outer);
        }

        sut.Current.Should().BeNull();
        sut.ResolveEmbeddingDelay(TimeSpan.FromMilliseconds(7))
            .Should().Be(TimeSpan.FromMilliseconds(7));
    }
}
