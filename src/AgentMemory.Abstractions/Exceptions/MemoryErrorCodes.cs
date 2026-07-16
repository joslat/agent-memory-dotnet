namespace AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Well-known structured error codes for all memory system exceptions.
/// Use with <see cref="MemoryErrorBuilder.WithCode"/> to attach a code to a <see cref="MemoryException"/>.
/// </summary>
public static class MemoryErrorCodes
{
    /// <summary>An entity lookup by ID returned no result.</summary>
    public const string EntityNotFound          = "MEMORY_ENTITY_NOT_FOUND";

    /// <summary>A fact lookup by ID returned no result.</summary>
    public const string FactNotFound            = "MEMORY_FACT_NOT_FOUND";

    /// <summary>A preference lookup by ID returned no result.</summary>
    public const string PreferenceNotFound      = "MEMORY_PREFERENCE_NOT_FOUND";

    /// <summary>A reasoning trace lookup by ID returned no result.</summary>
    public const string TraceNotFound           = "MEMORY_TRACE_NOT_FOUND";

    /// <summary>An entity already exists with the same identity.</summary>
    public const string DuplicateEntity         = "MEMORY_DUPLICATE_ENTITY";

    /// <summary>Generating an embedding vector failed.</summary>
    public const string EmbeddingFailed         = "MEMORY_EMBEDDING_FAILED";

    /// <summary>The extraction pipeline failed to extract memory from messages.</summary>
    public const string ExtractionFailed        = "MEMORY_EXTRACTION_FAILED";

    /// <summary>Schema bootstrap (constraints, indexes) failed on startup.</summary>
    public const string SchemaBootstrapFailed   = "MEMORY_SCHEMA_BOOTSTRAP_FAILED";

    /// <summary>An existing vector index's dimensionality does not match the configured embedder.</summary>
    public const string EmbeddingDimensionMismatch = "MEMORY_EMBEDDING_DIMENSION_MISMATCH";

    /// <summary>A Neo4j transaction was rolled back or could not be committed.</summary>
    public const string TransactionFailed       = "MEMORY_TRANSACTION_FAILED";

    /// <summary>A graph query (Cypher) failed unexpectedly.</summary>
    public const string QueryFailed             = "MEMORY_QUERY_FAILED";

    /// <summary>Input validation failed before a write was attempted.</summary>
    public const string ValidationFailed        = "MEMORY_VALIDATION_FAILED";

    /// <summary>The assembled context exceeded its configured character budget.</summary>
    public const string ContextBudgetExceeded   = "MEMORY_CONTEXT_BUDGET_EXCEEDED";

    /// <summary>A required configuration value is missing or invalid.</summary>
    public const string ConfigurationInvalid    = "MEMORY_CONFIGURATION_INVALID";

    /// <summary>Entity resolution (matching or deduplication) failed.</summary>
    public const string ResolutionFailed        = "MEMORY_RESOLUTION_FAILED";

    /// <summary>Persisting one or more memory items to the graph failed.</summary>
    public const string PersistenceFailed       = "MEMORY_PERSISTENCE_FAILED";

    // ── Ingestion outcome codes (#101) — used as IngestionItemOutcome.ErrorCode ──

    /// <summary>An entity extractor threw.</summary>
    public const string EntityExtractionFailed       = "MEMORY_ENTITY_EXTRACTION_FAILED";

    /// <summary>A fact extractor threw.</summary>
    public const string FactExtractionFailed         = "MEMORY_FACT_EXTRACTION_FAILED";

    /// <summary>A preference extractor threw.</summary>
    public const string PreferenceExtractionFailed   = "MEMORY_PREFERENCE_EXTRACTION_FAILED";

    /// <summary>A relationship extractor threw.</summary>
    public const string RelationshipExtractionFailed = "MEMORY_RELATIONSHIP_EXTRACTION_FAILED";

    /// <summary>An extracted entity candidate failed <c>EntityValidator</c> validation.</summary>
    public const string EntityValidationFailed       = "MEMORY_ENTITY_VALIDATION_FAILED";

    /// <summary>Entity resolution (matching/deduplication) threw for one candidate.</summary>
    public const string EntityResolutionFailed       = "MEMORY_ENTITY_RESOLUTION_FAILED";

    /// <summary>A relationship's source or target entity was never resolved during extraction.</summary>
    public const string RelationshipEndpointUnresolved = "MEMORY_RELATIONSHIP_ENDPOINT_UNRESOLVED";

    /// <summary>Generating an embedding for an item to persist failed.</summary>
    public const string EmbeddingGenerationFailed    = "MEMORY_EMBEDDING_GENERATION_FAILED";

    /// <summary>Persisting an entity node failed.</summary>
    public const string EntityPersistenceFailed       = "MEMORY_ENTITY_PERSISTENCE_FAILED";

    /// <summary>Persisting a fact node failed.</summary>
    public const string FactPersistenceFailed         = "MEMORY_FACT_PERSISTENCE_FAILED";

    /// <summary>Persisting a preference node failed.</summary>
    public const string PreferencePersistenceFailed   = "MEMORY_PREFERENCE_PERSISTENCE_FAILED";

    /// <summary>Persisting a relationship edge failed.</summary>
    public const string RelationshipPersistenceFailed = "MEMORY_RELATIONSHIP_PERSISTENCE_FAILED";

    /// <summary>A relationship's source or target entity was never persisted.</summary>
    public const string RelationshipEndpointNotPersisted = "MEMORY_RELATIONSHIP_ENDPOINT_NOT_PERSISTED";

    /// <summary>Creating an <c>EXTRACTED_FROM</c> provenance edge failed for an otherwise-persisted item.</summary>
    public const string ProvenancePersistenceFailed   = "MEMORY_PROVENANCE_PERSISTENCE_FAILED";
}
