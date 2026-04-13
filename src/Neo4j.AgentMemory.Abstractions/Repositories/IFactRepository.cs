using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for fact persistence.
/// </summary>
public interface IFactRepository
{
    /// <summary>Adds or updates a fact.</summary>
    Task<Fact> UpsertAsync(Fact fact, CancellationToken cancellationToken = default);

    /// <summary>Gets a fact by identifier.</summary>
    Task<Fact?> GetByIdAsync(string factId, CancellationToken cancellationToken = default);

    /// <summary>Gets facts by subject.</summary>
    Task<IReadOnlyList<Fact>> GetBySubjectAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>Searches facts by vector similarity.</summary>
    Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>Adds or updates a batch of facts atomically.</summary>
    Task<IReadOnlyList<Fact>> UpsertBatchAsync(IReadOnlyList<Fact> facts, CancellationToken cancellationToken = default);

    /// <summary>Creates an EXTRACTED_FROM relationship from a fact to a source message.</summary>
    Task CreateExtractedFromRelationshipAsync(string factId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Creates an ABOUT relationship from a fact to an entity.</summary>
    Task CreateAboutRelationshipAsync(string factId, string entityId, CancellationToken cancellationToken = default);

    /// <summary>Creates a HAS_FACT relationship from a conversation to this fact.</summary>
    Task CreateConversationFactRelationshipAsync(string conversationId, string factId, CancellationToken cancellationToken = default);
}
