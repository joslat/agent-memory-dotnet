using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// Matches entities using cosine similarity of embedding vectors.
/// Only compares against entities that have a non-null Embedding.
/// </summary>
internal sealed class SemanticMatchEntityMatcher : IEntityMatcher
{
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly EntityResolutionOptions _options;

    public SemanticMatchEntityMatcher(
        IEmbeddingOrchestrator embeddingOrchestrator,
        EntityResolutionOptions options)
    {
        _embeddingOrchestrator = embeddingOrchestrator;
        _options = options;
    }

    public EntityMatchType MatchType => EntityMatchType.Semantic;

    public async Task<EntityResolutionResult?> TryMatchAsync(
        ExtractedEntity candidate,
        IReadOnlyList<Entity> existingEntities,
        CancellationToken cancellationToken = default)
    {
        var candidateEmbedding = await _embeddingOrchestrator
            .EmbedEntityAsync(candidate.Name, cancellationToken)
            .ConfigureAwait(false);

        // The embedding orchestrator degrades to an empty vector on a generation failure (transient
        // provider outage / rate limit). Treat that as "no semantic signal" and skip matching entirely,
        // so resolution falls through to CreateNew and the entity (plus its relationships) is still
        // persisted — rather than throwing a dimension-mismatch out of the resolver, which would silently
        // drop the entity and every edge that references it.
        if (candidateEmbedding.Length == 0)
            return null;

        Entity? bestEntity = null;
        double bestScore = _options.SemanticMatchThreshold;

        foreach (var existing in existingEntities)
        {
            // Skip candidates with no embedding, or whose stored vector has a different dimensionality
            // (e.g. a partially back-filled node, or a node embedded by a different model). Comparing
            // mismatched dimensions can only error, never match.
            if (existing.Embedding is null || existing.Embedding.Length != candidateEmbedding.Length)
                continue;

            var similarity = CosineSimilarity(candidateEmbedding, existing.Embedding);
            if (similarity > bestScore)
            {
                bestScore = similarity;
                bestEntity = existing;
            }
        }

        if (bestEntity is null)
            return null;

        return new EntityResolutionResult
        {
            ResolvedEntity = bestEntity,
            MatchType = MatchType,
            Confidence = bestScore
        };
    }

    /// <summary>
    /// Computes cosine similarity between two vectors.
    /// Returns value in range [-1, 1]; higher is more similar.
    /// </summary>
    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Embedding dimensions must match.");

        double dot = 0.0, normA = 0.0, normB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        if (normA == 0.0 || normB == 0.0)
            return 0.0;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
