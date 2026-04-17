namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Full provenance information for an entity, including source messages and extractors.
/// </summary>
public sealed record EntityProvenance(
    string EntityId,
    IReadOnlyList<ProvenanceSource> Sources,
    IReadOnlyList<ProvenanceExtractor> Extractors);

/// <summary>
/// A source message from which an entity was extracted.
/// </summary>
public sealed record ProvenanceSource(string MessageId, double? Confidence, int? StartPos, int? EndPos);

/// <summary>
/// An extractor that produced an entity.
/// </summary>
public sealed record ProvenanceExtractor(string ExtractorName, double Confidence, int? ExtractionTimeMs);
