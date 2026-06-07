namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Render-ready result of a memory query/command performed through <see cref="IMemoryQueryFacade"/>.
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Text">The render-ready text to surface to the caller (empty on failure).</param>
/// <param name="Error">A human-readable error/validation message when <paramref name="Success"/> is false.</param>
public readonly record struct MemoryQueryResult(bool Success, string Text, string? Error)
{
    /// <summary>Creates a successful result carrying render-ready <paramref name="text"/>.</summary>
    public static MemoryQueryResult Ok(string text) => new(true, text, null);

    /// <summary>Creates a failed result carrying an <paramref name="error"/> message.</summary>
    public static MemoryQueryResult Failed(string error) => new(false, string.Empty, error);
}

/// <summary>
/// Core facade that owns the search/compose/format business logic behind the agent-memory tools.
/// Framework adapters (MAF tools, Semantic Kernel plugin, MCP server) delegate to this facade and
/// only map its <see cref="MemoryQueryResult"/> onto their respective framework types — they hold
/// no search, persistence, or formatting logic themselves.
/// </summary>
public interface IMemoryQueryFacade
{
    /// <summary>Semantic search across entities, facts, and preferences; returns a formatted summary.</summary>
    Task<MemoryQueryResult> SearchMemoryAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Stores a user preference under the given category (default <c>"general"</c>).</summary>
    Task<MemoryQueryResult> RememberPreferenceAsync(string preferenceText, string category = "general", CancellationToken cancellationToken = default);

    /// <summary>Stores a subject-predicate-object fact.</summary>
    Task<MemoryQueryResult> RememberFactAsync(string subject, string predicate, string @object, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves preferences by <paramref name="category"/>, or by semantic <paramref name="query"/>
    /// when no category is supplied.
    /// </summary>
    Task<MemoryQueryResult> RecallPreferencesAsync(string? category, string? query, CancellationToken cancellationToken = default);

    /// <summary>Searches the knowledge graph for entities matching the query.</summary>
    Task<MemoryQueryResult> SearchKnowledgeAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Searches reasoning traces for similar past tasks.</summary>
    Task<MemoryQueryResult> FindSimilarTasksAsync(string taskDescription, CancellationToken cancellationToken = default);
}
