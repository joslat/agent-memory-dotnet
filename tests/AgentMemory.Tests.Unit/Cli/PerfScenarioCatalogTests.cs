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

    [Fact]
    public void Catalog_ContainsDegradedRecallScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-R-07");

        scenario.Description.Should().ContainEquivalentOf("degraded");
        scenario.Description.Should().ContainEquivalentOf("dependency");
        scenario.SupportsInterleavedAb.Should().BeTrue(
            "the recall fixture is isolated per A/B arm and the dependency preset is scenario-scoped");
    }

    [Fact]
    public void Select_DegradedRecallScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-R-07");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-R-07");
    }

    [Fact]
    public void Catalog_ContainsGraphRagRecallScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-R-08");

        scenario.Description.Should().ContainEquivalentOf("GraphRAG");
        scenario.Description.Should().ContainEquivalentOf("recall");
        scenario.SupportsInterleavedAb.Should().BeTrue(
            "the GraphRAG source is deterministic and the recall fixture is isolated per A/B arm");
    }

    [Fact]
    public void Select_GraphRagRecallScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-R-08");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-R-08");
    }

    [Fact]
    public void Catalog_ContainsWholeSessionExtractionScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-W-05");

        scenario.Description.Should().ContainEquivalentOf("session");
        scenario.Description.Should().Contain("50");
        scenario.SupportsInterleavedAb.Should().BeFalse(
            "the scenario persists extracted memories and cannot share mutable state between A/B arms");
        scenario.SetupAsync.Should().NotBeNull(
            "the 50 source messages must be seeded outside the measured turn");
    }

    [Fact]
    public void Select_WholeSessionExtractionScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-W-05");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-W-05");
    }

    [Fact]
    public void Catalog_ContainsRawBatchStorageScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-W-06");

        scenario.Description.Should().ContainEquivalentOf("50");
        scenario.Description.Should().ContainEquivalentOf("raw");
        scenario.Description.Should().ContainEquivalentOf("embedding");
        scenario.SupportsInterleavedAb.Should().BeFalse(
            "the scenario persists messages and cannot share mutable state between A/B arms");
        scenario.SetupAsync.Should().BeNull(
            "the measured operation must include raw message embedding and persistence");
        scenario.VerifyAsync.Should().NotBeNull(
            "the scenario must read the stored messages back outside the measured turn");
    }

    [Fact]
    public void Select_RawBatchStorageScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-W-06");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-W-06");
    }
}
