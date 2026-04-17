using System.Threading;
using System.Threading.Tasks;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Centralizes embedding generation for all memory domain concepts.
/// </summary>
public interface IEmbeddingOrchestrator
{
    /// <summary>Generates an embedding vector for an entity name.</summary>
    Task<float[]> EmbedEntityAsync(string entityName, CancellationToken ct = default);

    /// <summary>Generates an embedding vector for a Subject-Predicate-Object fact triple.</summary>
    Task<float[]> EmbedFactAsync(string subject, string predicate, string obj, CancellationToken ct = default);

    /// <summary>Generates an embedding vector for a user preference.</summary>
    Task<float[]> EmbedPreferenceAsync(string preferenceText, CancellationToken ct = default);

    /// <summary>Generates an embedding vector for a conversation message.</summary>
    Task<float[]> EmbedMessageAsync(string content, CancellationToken ct = default);

    /// <summary>Generates an embedding vector for a recall query.</summary>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default);

    /// <summary>Generates an embedding vector for arbitrary text.</summary>
    Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default);
}
