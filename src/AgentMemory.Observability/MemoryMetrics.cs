using System.Diagnostics.Metrics;

namespace AgentMemory.Observability;

/// <summary>
/// Centralized <see cref="Meter"/> with counters and histograms for memory operations.
/// </summary>
public sealed class MemoryMetrics : IDisposable
{
    /// <summary>
    /// The meter name used when registering with OpenTelemetry.
    /// </summary>
    public const string MeterName = "AgentMemory";

    private readonly Meter _meter;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryMetrics"/> class.
    /// </summary>
    internal MemoryMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        MessagesStored = _meter.CreateCounter<long>(
            "memory.messages.stored",
            description: "Number of messages stored in short-term memory");

        EntitiesExtracted = _meter.CreateCounter<long>(
            "memory.entities.extracted",
            description: "Number of entities extracted from messages");

        FactsExtracted = _meter.CreateCounter<long>(
            "memory.facts.extracted",
            description: "Number of facts extracted from messages");

        PreferencesExtracted = _meter.CreateCounter<long>(
            "memory.preferences.extracted",
            description: "Number of preferences extracted from messages");

        RecallRequests = _meter.CreateCounter<long>(
            "memory.recall.requests",
            description: "Number of recall operations performed");

        ExtractionErrors = _meter.CreateCounter<long>(
            "memory.extraction.errors",
            description: "Number of extraction operations that failed");

        GraphRagQueries = _meter.CreateCounter<long>(
            "memory.graphrag.queries",
            description: "Number of GraphRAG context queries performed");

        RecallDurationMs = _meter.CreateHistogram<double>(
            "memory.recall.duration",
            unit: "ms",
            description: "Duration of recall operations in milliseconds");

        ExtractionDurationMs = _meter.CreateHistogram<double>(
            "memory.extraction.duration",
            unit: "ms",
            description: "Duration of extraction operations in milliseconds");

        PersistDurationMs = _meter.CreateHistogram<double>(
            "memory.persist.duration",
            unit: "ms",
            description: "Duration of persist operations in milliseconds");

        GraphRagDurationMs = _meter.CreateHistogram<double>(
            "memory.graphrag.duration",
            unit: "ms",
            description: "Duration of GraphRAG queries in milliseconds");

        ContextAssemblyDurationMs = _meter.CreateHistogram<double>(
            "memory.context_assembly.duration",
            unit: "ms",
            description: "Duration of context assembly operations in milliseconds");

        EntityExtractionDurationMs = _meter.CreateHistogram<double>(
            "memory.entity_extraction.duration",
            unit: "ms",
            description: "Duration of entity extraction operations in milliseconds");

        FactExtractionDurationMs = _meter.CreateHistogram<double>(
            "memory.fact_extraction.duration",
            unit: "ms",
            description: "Duration of fact extraction operations in milliseconds");

        PreferenceExtractionDurationMs = _meter.CreateHistogram<double>(
            "memory.preference_extraction.duration",
            unit: "ms",
            description: "Duration of preference extraction operations in milliseconds");

        RelationshipExtractionDurationMs = _meter.CreateHistogram<double>(
            "memory.relationship_extraction.duration",
            unit: "ms",
            description: "Duration of relationship extraction operations in milliseconds");

        RelationshipsExtracted = _meter.CreateCounter<long>(
            "memory.relationships.extracted",
            description: "Number of relationships extracted from messages");

        EnrichmentRequests = _meter.CreateCounter<long>(
            "memory.enrichment.requests",
            description: "Number of enrichment requests performed");

        EnrichmentErrors = _meter.CreateCounter<long>(
            "memory.enrichment.errors",
            description: "Number of enrichment operations that failed");

        EnrichmentDurationMs = _meter.CreateHistogram<double>(
            "memory.enrichment.duration",
            unit: "ms",
            description: "Duration of enrichment operations in milliseconds");
    }

    /// <summary>
    /// Disposes the underlying <see cref="Meter"/>.
    /// </summary>
    public void Dispose() => _meter.Dispose();

    // Counters

    /// <summary>Number of messages stored in short-term memory.</summary>
    internal Counter<long> MessagesStored { get; }

    /// <summary>Number of entities extracted from messages.</summary>
    internal Counter<long> EntitiesExtracted { get; }

    /// <summary>Number of facts extracted from messages.</summary>
    internal Counter<long> FactsExtracted { get; }

    /// <summary>Number of preferences extracted from messages.</summary>
    internal Counter<long> PreferencesExtracted { get; }

    /// <summary>Number of recall operations performed.</summary>
    internal Counter<long> RecallRequests { get; }

    /// <summary>Number of extraction operations that failed.</summary>
    internal Counter<long> ExtractionErrors { get; }

    /// <summary>Number of GraphRAG context queries performed.</summary>
    internal Counter<long> GraphRagQueries { get; }

    // Histograms

    /// <summary>Duration of recall operations in milliseconds.</summary>
    internal Histogram<double> RecallDurationMs { get; }

    /// <summary>Duration of extraction operations in milliseconds.</summary>
    internal Histogram<double> ExtractionDurationMs { get; }

    /// <summary>Duration of persist operations in milliseconds.</summary>
    internal Histogram<double> PersistDurationMs { get; }

    /// <summary>Duration of GraphRAG queries in milliseconds.</summary>
    internal Histogram<double> GraphRagDurationMs { get; }

    /// <summary>Duration of context assembly operations in milliseconds.</summary>
    internal Histogram<double> ContextAssemblyDurationMs { get; }

    /// <summary>Duration of entity extraction operations in milliseconds.</summary>
    internal Histogram<double> EntityExtractionDurationMs { get; }

    /// <summary>Duration of fact extraction operations in milliseconds.</summary>
    internal Histogram<double> FactExtractionDurationMs { get; }

    /// <summary>Duration of preference extraction operations in milliseconds.</summary>
    internal Histogram<double> PreferenceExtractionDurationMs { get; }

    /// <summary>Duration of relationship extraction operations in milliseconds.</summary>
    internal Histogram<double> RelationshipExtractionDurationMs { get; }

    /// <summary>Number of relationships extracted from messages.</summary>
    internal Counter<long> RelationshipsExtracted { get; }

    /// <summary>Number of enrichment requests performed.</summary>
    internal Counter<long> EnrichmentRequests { get; }

    /// <summary>Number of enrichment operations that failed.</summary>
    internal Counter<long> EnrichmentErrors { get; }

    /// <summary>Duration of enrichment operations in milliseconds.</summary>
    internal Histogram<double> EnrichmentDurationMs { get; }
}
