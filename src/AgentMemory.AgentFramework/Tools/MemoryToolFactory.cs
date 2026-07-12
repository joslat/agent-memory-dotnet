using System.ComponentModel;
using Microsoft.Extensions.AI;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.AgentFramework.Tools;

/// <summary>
/// Creates callable memory tools for MAF agents as <see cref="AIFunction"/> instances. A thin adapter:
/// every capability delegates to the Core <see cref="IMemoryQueryFacade"/>, which owns all
/// search/persistence/formatting logic.
/// </summary>
public sealed class MemoryToolFactory
{
    private readonly IMemoryQueryFacade _facade;

    public MemoryToolFactory(IMemoryQueryFacade facade)
    {
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
    }

    /// <summary>
    /// Returns the 6 standard memory tools as MAF-compatible <see cref="AIFunction"/> instances,
    /// suitable for registration with <c>ChatClientAgentOptions.ChatOptions.Tools</c> or
    /// <c>.AsAIAgent(tools: [...])</c>.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateAIFunctions() =>
    [
        AIFunctionFactory.Create(SearchMemoryAsync, "search_memory",
            "Semantic search across all memory layers (entities, facts, preferences)."),
        AIFunctionFactory.Create(RememberPreferenceAsync, "remember_preference",
            "Store a user preference with an optional category."),
        AIFunctionFactory.Create(RememberFactAsync, "remember_fact",
            "Store a fact as a subject-predicate-object triple."),
        AIFunctionFactory.Create(RecallPreferencesAsync, "recall_preferences",
            "Retrieve stored preferences, optionally filtered by category."),
        AIFunctionFactory.Create(SearchKnowledgeAsync, "search_knowledge",
            "Search entities and relationships in the knowledge graph."),
        AIFunctionFactory.Create(FindSimilarTasksAsync, "find_similar_tasks",
            "Search reasoning traces for similar past tasks."),
    ];

    // ──────────────────────────────────────────────────────────────────────────
    // AIFunction-compatible methods (used by CreateAIFunctions via AIFunctionFactory.Create).
    // Each has [Description] attributes so MEAI generates a proper JSON schema, and each simply
    // renders the facade's result.
    // ──────────────────────────────────────────────────────────────────────────

    private async Task<string> SearchMemoryAsync(
        [Description("The search query to find relevant memories, entities, facts, and preferences.")] string query,
        CancellationToken cancellationToken = default)
        => Render(await _facade.SearchMemoryAsync(query, cancellationToken).ConfigureAwait(false));

    private async Task<string> RememberPreferenceAsync(
        [Description("The preference text to store (e.g. 'prefers dark mode').")] string preferenceText,
        [Description("The preference category (e.g. 'style', 'language'). Defaults to 'general'.")] string category = "general",
        CancellationToken cancellationToken = default)
        => Render(await _facade.RememberPreferenceAsync(preferenceText, category, cancellationToken).ConfigureAwait(false));

    private async Task<string> RememberFactAsync(
        [Description("The subject of the fact (e.g. 'Alice').")] string subject,
        [Description("The predicate/relationship (e.g. 'works_at', 'likes').")] string predicate,
        [Description("The object/value of the fact (e.g. 'Acme Corp').")] string @object,
        CancellationToken cancellationToken = default)
        => Render(await _facade.RememberFactAsync(subject, predicate, @object, cancellationToken).ConfigureAwait(false));

    private async Task<string> RecallPreferencesAsync(
        [Description("Optional category filter (e.g. 'style'). If empty, performs a semantic search using query.")] string? category = null,
        [Description("Semantic search query used when no category is provided.")] string? query = null,
        CancellationToken cancellationToken = default)
        => Render(await _facade.RecallPreferencesAsync(category, query, cancellationToken).ConfigureAwait(false));

    private async Task<string> SearchKnowledgeAsync(
        [Description("The query to search the knowledge graph for entities and relationships.")] string query,
        CancellationToken cancellationToken = default)
        => Render(await _facade.SearchKnowledgeAsync(query, cancellationToken).ConfigureAwait(false));

    private async Task<string> FindSimilarTasksAsync(
        [Description("Description of the task to find similar past reasoning traces for.")] string taskDescription,
        CancellationToken cancellationToken = default)
        => Render(await _facade.FindSimilarTasksAsync(taskDescription, cancellationToken).ConfigureAwait(false));

    private static string Render(MemoryQueryResult result) =>
        result.Success ? result.Text : result.Error ?? "Operation failed.";
}
