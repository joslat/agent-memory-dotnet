namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Domain-specific convenience helpers over <see cref="IEmbeddingOrchestrator.EmbedAsync"/>. These
/// preserve the previous typed API (<c>EmbedEntityAsync</c>, <c>EmbedFactAsync</c>, …) as thin,
/// allocation-free wrappers so call sites read intentionally while the interface stays minimal.
/// </summary>
public static class EmbeddingOrchestratorExtensions
{
    /// <summary>Embeds an entity name.</summary>
    public static Task<float[]> EmbedEntityAsync(this IEmbeddingOrchestrator orchestrator, string entityName, CancellationToken cancellationToken = default)
        => orchestrator.EmbedAsync(entityName, cancellationToken);

    /// <summary>Embeds a Subject-Predicate-Object fact triple as a single composed string.</summary>
    public static Task<float[]> EmbedFactAsync(this IEmbeddingOrchestrator orchestrator, string subject, string predicate, string obj, CancellationToken cancellationToken = default)
        => orchestrator.EmbedAsync($"{subject} {predicate} {obj}", cancellationToken);

    /// <summary>Embeds a user preference.</summary>
    public static Task<float[]> EmbedPreferenceAsync(this IEmbeddingOrchestrator orchestrator, string preferenceText, CancellationToken cancellationToken = default)
        => orchestrator.EmbedAsync(preferenceText, cancellationToken);

    /// <summary>Embeds a conversation message.</summary>
    public static Task<float[]> EmbedMessageAsync(this IEmbeddingOrchestrator orchestrator, string content, CancellationToken cancellationToken = default)
        => orchestrator.EmbedAsync(content, cancellationToken);

    /// <summary>Embeds a recall query.</summary>
    public static Task<float[]> EmbedQueryAsync(this IEmbeddingOrchestrator orchestrator, string query, CancellationToken cancellationToken = default)
        => orchestrator.EmbedAsync(query, cancellationToken);

    /// <summary>Embeds arbitrary text. Alias for <see cref="IEmbeddingOrchestrator.EmbedAsync"/>.</summary>
    public static Task<float[]> EmbedTextAsync(this IEmbeddingOrchestrator orchestrator, string text, CancellationToken cancellationToken = default)
        => orchestrator.EmbedAsync(text, cancellationToken);
}
