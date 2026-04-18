using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.AgentFramework.Tools;

/// <summary>
/// Creates callable memory tools for MAF agents. A thin adapter that delegates to Core services.
/// </summary>
public sealed class MemoryToolFactory
{
    private readonly ILongTermMemoryService _longTermService;
    private readonly IReasoningMemoryService _reasoningService;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<MemoryToolFactory> _logger;

    public MemoryToolFactory(
        ILongTermMemoryService longTermService,
        IReasoningMemoryService reasoningService,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<MemoryToolFactory> logger)
    {
        _longTermService = longTermService ?? throw new ArgumentNullException(nameof(longTermService));
        _reasoningService = reasoningService ?? throw new ArgumentNullException(nameof(reasoningService));
        _embeddingOrchestrator = embeddingOrchestrator ?? throw new ArgumentNullException(nameof(embeddingOrchestrator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    /// <summary>Returns the 6 standard memory tools as <see cref="MemoryTool"/> objects.</summary>
    /// <remarks>For MAF function-calling tool slots, prefer <see cref="CreateAIFunctions"/> instead.</remarks>
    [Obsolete("Use CreateAIFunctions() to get MAF-compatible AIFunction instances for tool registration.")]
    public IReadOnlyList<MemoryTool> CreateTools() =>
    [
        CreateSearchMemoryTool(),
        CreateRememberPreferenceTool(),
        CreateRememberFactTool(),
        CreateRecallPreferencesTool(),
        CreateSearchKnowledgeTool(),
        CreateFindSimilarTasksTool(),
    ];

    private MemoryTool CreateSearchMemoryTool() => new()
    {
        Name = "search_memory",
        Description = "Semantic search across all memory layers (entities, facts, preferences).",
        ExecuteAsync = async (request, ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Fail("Query is required for search_memory.");
            try
            {
                var embedding = await _embeddingOrchestrator.EmbedQueryAsync(request.Query, ct);
                var entities = await _longTermService.SearchEntitiesAsync(embedding, cancellationToken: ct);
                var facts = await _longTermService.SearchFactsAsync(embedding, cancellationToken: ct);
                var preferences = await _longTermService.SearchPreferencesAsync(embedding, cancellationToken: ct);

                var sb = new StringBuilder();
                if (entities.Count > 0)
                {
                    sb.AppendLine("Entities:");
                    foreach (var e in entities)
                        sb.AppendLine($"  [{e.Type}] {e.Name}: {e.Description}");
                }
                if (facts.Count > 0)
                {
                    sb.AppendLine("Facts:");
                    foreach (var f in facts)
                        sb.AppendLine($"  {f.Subject} {f.Predicate} {f.Object}");
                }
                if (preferences.Count > 0)
                {
                    sb.AppendLine("Preferences:");
                    foreach (var p in preferences)
                        sb.AppendLine($"  [{p.Category}] {p.PreferenceText}");
                }
                return Ok(sb.Length > 0 ? sb.ToString().Trim() : "No results found.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "search_memory failed for query: {Query}", request.Query);
                return Fail(ex.Message);
            }
        }
    };

    private MemoryTool CreateRememberPreferenceTool() => new()
    {
        Name = "remember_preference",
        Description = "Store a user preference.",
        ExecuteAsync = async (request, ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Fail("Query (preference text) is required for remember_preference.");
            try
            {
                var category = GetParameter(request, "category") ?? "general";
                var preference = new Preference
                {
                    PreferenceId = _idGenerator.GenerateId(),
                    Category = category,
                    PreferenceText = request.Query,
                    Confidence = 1.0,
                    CreatedAtUtc = _clock.UtcNow,
                };
                await _longTermService.AddPreferenceAsync(preference, ct);
                return Ok($"Preference stored: [{category}] {request.Query}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "remember_preference failed.");
                return Fail(ex.Message);
            }
        }
    };

    private MemoryTool CreateRememberFactTool() => new()
    {
        Name = "remember_fact",
        Description = "Store a fact as subject-predicate-object triple.",
        ExecuteAsync = async (request, ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Fail("Query is required for remember_fact.");
            try
            {
                var subject = GetParameter(request, "subject") ?? request.Query;
                var predicate = GetParameter(request, "predicate") ?? "relates_to";
                var obj = GetParameter(request, "object") ?? string.Empty;
                var fact = new Fact
                {
                    FactId = _idGenerator.GenerateId(),
                    Subject = subject,
                    Predicate = predicate,
                    Object = obj,
                    Confidence = 1.0,
                    CreatedAtUtc = _clock.UtcNow,
                };
                await _longTermService.AddFactAsync(fact, ct);
                return Ok($"Fact stored: {subject} {predicate} {obj}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "remember_fact failed.");
                return Fail(ex.Message);
            }
        }
    };

    private MemoryTool CreateRecallPreferencesTool() => new()
    {
        Name = "recall_preferences",
        Description = "Retrieve stored preferences, optionally filtered by category.",
        ExecuteAsync = async (request, ct) =>
        {
            try
            {
                var category = GetParameter(request, "category");
                IReadOnlyList<Preference> preferences;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    preferences = await _longTermService.GetPreferencesByCategoryAsync(category, ct);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(request.Query))
                        return Fail("Query or category is required for recall_preferences.");
                    var embedding = await _embeddingOrchestrator.EmbedQueryAsync(request.Query, ct);
                    preferences = await _longTermService.SearchPreferencesAsync(embedding, cancellationToken: ct);
                }
                if (preferences.Count == 0)
                    return Ok("No preferences found.");
                var sb = new StringBuilder();
                foreach (var p in preferences)
                    sb.AppendLine($"[{p.Category}] {p.PreferenceText}");
                return Ok(sb.ToString().Trim());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "recall_preferences failed.");
                return Fail(ex.Message);
            }
        }
    };

    private MemoryTool CreateSearchKnowledgeTool() => new()
    {
        Name = "search_knowledge",
        Description = "Search entities and relationships in the knowledge graph.",
        ExecuteAsync = async (request, ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Fail("Query is required for search_knowledge.");
            try
            {
                var embedding = await _embeddingOrchestrator.EmbedQueryAsync(request.Query, ct);
                var entities = await _longTermService.SearchEntitiesAsync(embedding, cancellationToken: ct);
                if (entities.Count == 0)
                    return Ok("No entities found.");
                var sb = new StringBuilder();
                foreach (var e in entities)
                    sb.AppendLine($"[{e.Type}] {e.Name}: {e.Description}");
                return Ok(sb.ToString().Trim());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "search_knowledge failed for query: {Query}", request.Query);
                return Fail(ex.Message);
            }
        }
    };

    private MemoryTool CreateFindSimilarTasksTool() => new()
    {
        Name = "find_similar_tasks",
        Description = "Search reasoning traces for similar past tasks.",
        ExecuteAsync = async (request, ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Fail("Query (task description) is required for find_similar_tasks.");
            try
            {
                var embedding = await _embeddingOrchestrator.EmbedQueryAsync(request.Query, ct);
                var traces = await _reasoningService.SearchSimilarTracesAsync(embedding, cancellationToken: ct);
                if (traces.Count == 0)
                    return Ok("No similar tasks found.");
                var sb = new StringBuilder();
                foreach (var t in traces)
                    sb.AppendLine($"[{(t.Success == true ? "✓" : "✗")}] {t.Task}: {t.Outcome}");
                return Ok(sb.ToString().Trim());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "find_similar_tasks failed for query: {Query}", request.Query);
                return Fail(ex.Message);
            }
        }
    };

    private static string? GetParameter(MemoryToolRequest request, string key) =>
        request.Parameters.TryGetValue(key, out var value) ? value.ToString() : null;

    private static MemoryToolResponse Ok(string result) => new() { Success = true, Result = result };
    private static MemoryToolResponse Fail(string error) => new() { Success = false, Error = error };

    // ──────────────────────────────────────────────────────────────────────────
    // AIFunction-compatible methods (used by CreateAIFunctions via AIFunctionFactory.Create)
    // Each method has [Description] attributes so MEAI generates a proper JSON schema.
    // ──────────────────────────────────────────────────────────────────────────

    private async Task<string> SearchMemoryAsync(
        [Description("The search query to find relevant memories, entities, facts, and preferences.")] string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Query is required for search_memory.";
        try
        {
            var embedding = await _embeddingOrchestrator.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
            var entities = await _longTermService.SearchEntitiesAsync(embedding, cancellationToken: cancellationToken).ConfigureAwait(false);
            var facts = await _longTermService.SearchFactsAsync(embedding, cancellationToken: cancellationToken).ConfigureAwait(false);
            var preferences = await _longTermService.SearchPreferencesAsync(embedding, cancellationToken: cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder();
            if (entities.Count > 0)
            {
                sb.AppendLine("Entities:");
                foreach (var e in entities) sb.AppendLine($"  [{e.Type}] {e.Name}: {e.Description}");
            }
            if (facts.Count > 0)
            {
                sb.AppendLine("Facts:");
                foreach (var f in facts) sb.AppendLine($"  {f.Subject} {f.Predicate} {f.Object}");
            }
            if (preferences.Count > 0)
            {
                sb.AppendLine("Preferences:");
                foreach (var p in preferences) sb.AppendLine($"  [{p.Category}] {p.PreferenceText}");
            }
            return sb.Length > 0 ? sb.ToString().Trim() : "No results found.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_memory failed for query: {Query}", query);
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> RememberPreferenceAsync(
        [Description("The preference text to store (e.g. 'prefers dark mode').")] string preferenceText,
        [Description("The preference category (e.g. 'style', 'language'). Defaults to 'general'.")] string category = "general",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preferenceText))
            return "preferenceText is required for remember_preference.";
        try
        {
            var preference = new Preference
            {
                PreferenceId = _idGenerator.GenerateId(),
                Category = category,
                PreferenceText = preferenceText,
                Confidence = 1.0,
                CreatedAtUtc = _clock.UtcNow,
            };
            await _longTermService.AddPreferenceAsync(preference, cancellationToken).ConfigureAwait(false);
            return $"Preference stored: [{category}] {preferenceText}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "remember_preference failed.");
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> RememberFactAsync(
        [Description("The subject of the fact (e.g. 'Alice').")] string subject,
        [Description("The predicate/relationship (e.g. 'works_at', 'likes').")] string predicate,
        [Description("The object/value of the fact (e.g. 'Acme Corp').")] string @object,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "subject is required for remember_fact.";
        try
        {
            var fact = new Fact
            {
                FactId = _idGenerator.GenerateId(),
                Subject = subject,
                Predicate = predicate,
                Object = @object,
                Confidence = 1.0,
                CreatedAtUtc = _clock.UtcNow,
            };
            await _longTermService.AddFactAsync(fact, cancellationToken).ConfigureAwait(false);
            return $"Fact stored: {subject} {predicate} {@object}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "remember_fact failed.");
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> RecallPreferencesAsync(
        [Description("Optional category filter (e.g. 'style'). If empty, performs a semantic search using query.")] string? category = null,
        [Description("Semantic search query used when no category is provided.")] string? query = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<Preference> preferences;
            if (!string.IsNullOrWhiteSpace(category))
            {
                preferences = await _longTermService.GetPreferencesByCategoryAsync(category, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(query))
                    return "Either category or query is required for recall_preferences.";
                var embedding = await _embeddingOrchestrator.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
                preferences = await _longTermService.SearchPreferencesAsync(embedding, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            if (preferences.Count == 0) return "No preferences found.";
            var sb = new StringBuilder();
            foreach (var p in preferences) sb.AppendLine($"[{p.Category}] {p.PreferenceText}");
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "recall_preferences failed.");
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> SearchKnowledgeAsync(
        [Description("The query to search the knowledge graph for entities and relationships.")] string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Query is required for search_knowledge.";
        try
        {
            var embedding = await _embeddingOrchestrator.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
            var entities = await _longTermService.SearchEntitiesAsync(embedding, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (entities.Count == 0) return "No entities found.";
            var sb = new StringBuilder();
            foreach (var e in entities) sb.AppendLine($"[{e.Type}] {e.Name}: {e.Description}");
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_knowledge failed for query: {Query}", query);
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> FindSimilarTasksAsync(
        [Description("Description of the task to find similar past reasoning traces for.")] string taskDescription,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskDescription))
            return "taskDescription is required for find_similar_tasks.";
        try
        {
            var embedding = await _embeddingOrchestrator.EmbedQueryAsync(taskDescription, cancellationToken).ConfigureAwait(false);
            var traces = await _reasoningService.SearchSimilarTracesAsync(embedding, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (traces.Count == 0) return "No similar tasks found.";
            var sb = new StringBuilder();
            foreach (var t in traces) sb.AppendLine($"[{(t.Success == true ? "✓" : "✗")}] {t.Task}: {t.Outcome}");
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "find_similar_tasks failed for query: {Query}", taskDescription);
            return $"Error: {ex.Message}";
        }
    }
}