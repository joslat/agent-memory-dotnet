namespace Neo4j.AgentMemory.Abstractions.Exceptions;

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
}
