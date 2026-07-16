namespace AgentMemory.Abstractions.Domain;

/// <summary>Pipeline stage at which an <see cref="IngestionItemOutcome"/> was recorded (#101).</summary>
public enum IngestionStage
{
    /// <summary>An extractor (entity/fact/preference/relationship) ran.</summary>
    Extraction,

    /// <summary>Entity validation (<c>EntityValidator</c>) ran against an extracted candidate.</summary>
    Validation,

    /// <summary>Entity resolution (matching/deduplication against existing graph entities) ran.</summary>
    Resolution,

    /// <summary>Embedding generation for a persisted item ran.</summary>
    Embedding,

    /// <summary>The item's node was upserted to its repository.</summary>
    Persistence,

    /// <summary>An <c>EXTRACTED_FROM</c> provenance edge was created for a persisted item.</summary>
    Provenance,

    /// <summary>A relationship's endpoints were checked against already-persisted entities.</summary>
    RelationshipPersistence
}
