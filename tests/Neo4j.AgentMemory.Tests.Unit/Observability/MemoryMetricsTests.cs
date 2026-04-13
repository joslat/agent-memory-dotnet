using System.Diagnostics.Metrics;
using FluentAssertions;
using Neo4j.AgentMemory.Observability;

namespace Neo4j.AgentMemory.Tests.Unit.Observability;

[Collection("Observability")]
public sealed class MemoryMetricsTests
{
    [Fact]
    public void MeterName_IsCorrect()
    {
        MemoryMetrics.MeterName.Should().Be("Neo4j.AgentMemory");
    }

    [Fact]
    public void Constructor_CreatesAllCounters()
    {
        var metrics = new MemoryMetrics();

        metrics.MessagesStored.Should().NotBeNull();
        metrics.EntitiesExtracted.Should().NotBeNull();
        metrics.FactsExtracted.Should().NotBeNull();
        metrics.PreferencesExtracted.Should().NotBeNull();
        metrics.RecallRequests.Should().NotBeNull();
        metrics.ExtractionErrors.Should().NotBeNull();
        metrics.GraphRagQueries.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_CreatesAllHistograms()
    {
        var metrics = new MemoryMetrics();

        metrics.RecallDurationMs.Should().NotBeNull();
        metrics.ExtractionDurationMs.Should().NotBeNull();
        metrics.PersistDurationMs.Should().NotBeNull();
        metrics.GraphRagDurationMs.Should().NotBeNull();
        metrics.ContextAssemblyDurationMs.Should().NotBeNull();
    }

    [Fact]
    public void Counters_CanBeIncremented()
    {
        var metrics = new MemoryMetrics();

        // Counters should accept values without throwing
        var act = () =>
        {
            metrics.MessagesStored.Add(1);
            metrics.EntitiesExtracted.Add(5);
            metrics.FactsExtracted.Add(3);
            metrics.PreferencesExtracted.Add(2);
            metrics.RecallRequests.Add(1);
            metrics.ExtractionErrors.Add(1);
            metrics.GraphRagQueries.Add(1);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Histograms_CanRecordValues()
    {
        var metrics = new MemoryMetrics();

        var act = () =>
        {
            metrics.RecallDurationMs.Record(42.5);
            metrics.ExtractionDurationMs.Record(100.0);
            metrics.PersistDurationMs.Record(55.3);
            metrics.GraphRagDurationMs.Record(200.1);
            metrics.ContextAssemblyDurationMs.Record(15.7);
        };

        act.Should().NotThrow();
    }
}
