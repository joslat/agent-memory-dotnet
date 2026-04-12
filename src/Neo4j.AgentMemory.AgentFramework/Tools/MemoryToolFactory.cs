using System.Text;
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
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<MemoryToolFactory> _logger;

    public MemoryToolFactory(
        ILongTermMemoryService longTermService,
        IReasoningMemoryService reasoningService,
        IEmbeddingProvider embeddingProvider,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<MemoryToolFactory> logger)
    {
        _longTermService = longTermService;
        _reasoningService = reasoningService;
        _embeddingProvider = embeddingProvider;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    /// <summary>Returns the 6 standard memory tools.</summary>
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
                var embedding = await _embeddingProvider.GenerateEmbeddingAsync(request.Query, ct);
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
                    var embedding = await _embeddingProvider.GenerateEmbeddingAsync(request.Query, ct);
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
                var embedding = await _embeddingProvider.GenerateEmbeddingAsync(request.Query, ct);
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
                var embedding = await _embeddingProvider.GenerateEmbeddingAsync(request.Query, ct);
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
}
