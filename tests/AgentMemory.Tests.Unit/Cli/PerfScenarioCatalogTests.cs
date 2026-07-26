using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class PerfScenarioCatalogTests
{
    [Fact]
    public void Catalog_ContainsGreetingScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-R-01");

        scenario.Description.Should().ContainEquivalentOf("greeting");
        scenario.Description.Should().ContainEquivalentOf("shipped default");
        scenario.SupportsInterleavedAb.Should().BeTrue(
            "the greeting scenario is read-only apart from the same access tracking isolated by perf ab");
    }

    [Fact]
    public void Select_GreetingScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-R-01");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-R-01");
    }

    [Fact]
    public void Catalog_ContainsToolHeavyWriteScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-W-03");

        scenario.Description.Should().ContainEquivalentOf("six");
        scenario.Description.Should().ContainEquivalentOf("tool-heavy");
        scenario.SupportsInterleavedAb.Should().BeFalse(
            "the scenario persists response messages and cannot share mutable state between A/B arms");
    }

    [Fact]
    public void Select_ToolHeavyWriteScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-W-03");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-W-03");
    }
}
