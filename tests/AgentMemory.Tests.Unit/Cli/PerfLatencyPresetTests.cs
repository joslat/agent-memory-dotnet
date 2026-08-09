using System.Reflection;
using AgentMemory.Cli.Commands;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class PerfLatencyPresetTests
{
    [Fact]
    public void ModelRemote_IsolatesModelDelayFromEmbeddingDelay()
    {
        var method = typeof(PerfCommand).GetMethod(
            "ResolveLatency",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        var result = ((TimeSpan Embedding, TimeSpan Model))method!.Invoke(null, ["model-remote"])!;

        result.Embedding.Should().Be(TimeSpan.Zero);
        result.Model.Should().Be(TimeSpan.FromMilliseconds(900));
    }
}
