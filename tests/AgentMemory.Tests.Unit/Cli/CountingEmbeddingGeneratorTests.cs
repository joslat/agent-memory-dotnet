using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class CountingEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_RecordsOneProviderSpanAndExactInputs()
    {
        using var collector = new PerfCollector();
        using var turn = collector.BeginTurn("test", 0, "measure");
        using var sut = new CountingEmbeddingGenerator(new DeterministicEmbeddingGenerator(8));

        var result = await sut.GenerateAsync(["alpha", "beta"]);

        result.Should().HaveCount(2);
        turn.Record.Counter("embed.requests").Should().Be(1);
        turn.Record.Counter("embed.items").Should().Be(2);
        turn.Record.SpanCounts.GetValueOrDefault("provider.embedding").Should().Be(1);
        turn.Record.SpanMilliseconds["provider.embedding"].Should().BeGreaterThanOrEqualTo(0);
    }
}
