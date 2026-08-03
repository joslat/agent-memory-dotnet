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

    [Fact]
    public void Catalog_ContainsExtractionOnlyScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-W-07");

        scenario.Description.Should().ContainEquivalentOf("four");
        scenario.Description.Should().ContainEquivalentOf("extraction");
        scenario.Description.Should().ContainEquivalentOf("persistence");
        scenario.SupportsInterleavedAb.Should().BeTrue(
            "the arm invokes pure extractor calls and creates no mutable graph state");
        scenario.SetupAsync.Should().BeNull(
            "the fixed source session is in-memory and must not add setup storage");
        scenario.VerifyAsync.Should().BeNull(
            "the measured body self-asserts exact results and every excluded dependency");
    }

    [Fact]
    public void Select_ExtractionOnlyScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-W-07");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-W-07");
    }

    [Fact]
    public void Catalog_ContainsFrozenPersistenceScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-W-08");

        scenario.Description.Should().ContainEquivalentOf("frozen");
        scenario.Description.Should().ContainEquivalentOf("persistence");
        scenario.SupportsInterleavedAb.Should().BeFalse(
            "the arm persists learned graph state under a unique owner/session");
        scenario.SetupAsync.Should().NotBeNull(
            "the one source message must be stored outside the measured turn");
        scenario.VerifyAsync.Should().NotBeNull(
            "learned graph shape and provenance must be read back outside the measured turn");
    }

    [Fact]
    public void Select_FrozenPersistenceScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-W-08");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-W-08");
    }

    [Fact]
    public void Catalog_ContainsUnifiedExtractionScenario_WithStableContract()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-W-09");

        scenario.Description.Should().ContainEquivalentOf("one");
        scenario.Description.Should().ContainEquivalentOf("unified");
        scenario.Description.Should().ContainEquivalentOf("extraction");
        scenario.Description.Should().ContainEquivalentOf("persistence");
        scenario.SupportsInterleavedAb.Should().BeTrue(
            "the arm invokes one pure extractor call and creates no mutable graph state");
        scenario.SetupAsync.Should().BeNull(
            "the fixed source session is in-memory and must not add setup storage");
        scenario.VerifyAsync.Should().BeNull(
            "the measured body self-asserts exact results and every excluded dependency");
    }

    [Fact]
    public void Select_UnifiedExtractionScenario_ReturnsOnlyThatScenario()
    {
        var selected = PerfScenarios.Select("PERF-W-09");

        selected.Should().ContainSingle();
        selected[0].Id.Should().Be("PERF-W-09");
    }

    [Fact]
    public void Catalog_ContainsFullPathColdBuildConcurrencyArms_WithStableContracts()
    {
        var expected = new[]
        {
            ("PERF-W-10-C01", 1),
            ("PERF-W-10-C05", 5),
            ("PERF-W-10-C10", 10),
        };

        foreach (var (id, workers) in expected)
        {
            var scenario = PerfScenarios.All.Single(item => item.Id == id);

            scenario.Description.Should().ContainEquivalentOf("cold-build");
            scenario.Description.Should().ContainEquivalentOf($"{workers} worker");
            scenario.SupportsInterleavedAb.Should().BeFalse(
                "each arm persists ten owner-isolated full-path units");
            scenario.SetupAsync.Should().BeNull();
            scenario.VerifyAsync.Should().NotBeNull(
                "graph shape, provenance, and owner isolation must be read back after the measured wave");
            scenario.IncludeInDefaultRun.Should().BeFalse(
                "lab-only full-path waves must not alter the committed default scenario catalog");
            scenario.RequiresUnifiedExtraction.Should().BeTrue(
                "the full-path lab measures the accepted one-call extraction candidate");
        }
    }

    [Fact]
    public void Catalog_ContainsTokenBoundedMultiSessionBatchArms_WithStableContracts()
    {
        var expected = new[]
        {
            ("PERF-W-11-B01", 1),
            ("PERF-W-11-B02", 2),
            ("PERF-W-11-B04", 4),
        };

        foreach (var (id, batchSize) in expected)
        {
            var scenario = PerfScenarios.All.Single(item => item.Id == id);

            scenario.Description.Should().ContainEquivalentOf("multi-session");
            scenario.Description.Should().ContainEquivalentOf($"batch size {batchSize}");
            scenario.SupportsInterleavedAb.Should().BeFalse(
                "each arm persists eight owner-isolated source sessions");
            scenario.SetupAsync.Should().BeNull();
            scenario.VerifyAsync.Should().NotBeNull(
                "graph shape, per-session provenance, ordering, and isolation must be read back");
            scenario.IncludeInDefaultRun.Should().BeFalse(
                "lab-only batching arms must not alter the committed default scenario catalog");
            scenario.RequiresUnifiedExtraction.Should().BeTrue();
        }
    }
}
