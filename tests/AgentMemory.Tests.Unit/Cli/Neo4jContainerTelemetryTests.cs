using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class Neo4jContainerTelemetryTests
{
    [Fact]
    public void Parser_ReadsDockerStatsJson_AndNormalizesCapacity()
    {
        const string json = """
            {"BlockIO":"296MB / 346MB","CPUPerc":"152.29%","Container":"probe","ID":"abc","MemPerc":"4.92%","MemUsage":"781.1MiB / 15.51GiB","Name":"probe","NetIO":"1.34kB / 248B","PIDs":"104"}
            """;

        Neo4jContainerStatsParser.TryParse(json, 20, out var sample).Should().BeTrue();

        sample.CpuRawPercent.Should().BeApproximately(152.29, 0.001);
        sample.CpuCapacityPercent.Should().BeApproximately(7.6145, 0.001);
        sample.MemoryUsedBytes.Should().Be(819_042_713);
        sample.MemoryLimitBytes.Should().Be(16_653_735_690);
        sample.MemoryPercent.Should().BeApproximately(4.92, 0.001);
        sample.BlockReadBytes.Should().Be(296_000_000);
        sample.BlockWriteBytes.Should().Be(346_000_000);
        sample.ProcessCount.Should().Be(104);
    }

    [Theory]
    [InlineData("0B", 0)]
    [InlineData("1kB", 1_000)]
    [InlineData("1MB", 1_000_000)]
    [InlineData("1GB", 1_000_000_000)]
    [InlineData("1KiB", 1_024)]
    [InlineData("1MiB", 1_048_576)]
    [InlineData("1GiB", 1_073_741_824)]
    public void Parser_ReadsDockerByteUnits(string text, long expected)
    {
        Neo4jContainerStatsParser.TryParseBytes(text, out var bytes).Should().BeTrue();
        bytes.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"CPUPerc\":\"?\"}")]
    public void Parser_RejectsIncompleteOrMalformedSamples(string text)
    {
        Neo4jContainerStatsParser.TryParse(text, 20, out _).Should().BeFalse();
    }
}
