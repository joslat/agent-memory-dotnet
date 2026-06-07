using System.Text;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Default <see cref="IMemoryQueryFacade"/>: owns the embed → search → format pipeline and the
/// store-preference/fact commands previously duplicated inside the framework adapters. Cancellation
/// is propagated; all other failures are logged and returned as a failed <see cref="MemoryQueryResult"/>.
/// </summary>
public sealed class MemoryQueryFacade : IMemoryQueryFacade
{
    private readonly ILongTermMemoryService _longTerm;
    private readonly IReasoningMemoryService _reasoning;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly IMemoryOwnerContext? _ownerContext;
    private readonly ILogger<MemoryQueryFacade> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryQueryFacade"/> class. The optional
    /// <paramref name="ownerContext"/> supplies the ambient owner/user for the LLM-invokable tools
    /// (the model cannot be trusted to pass a user id); null = unscoped/shared (IC8).
    /// </summary>
    public MemoryQueryFacade(
        ILongTermMemoryService longTerm,
        IReasoningMemoryService reasoning,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<MemoryQueryFacade> logger,
        IMemoryOwnerContext? ownerContext = null)
    {
        _longTerm = longTerm ?? throw new ArgumentNullException(nameof(longTerm));
        _reasoning = reasoning ?? throw new ArgumentNullException(nameof(reasoning));
        _embeddingOrchestrator = embeddingOrchestrator ?? throw new ArgumentNullException(nameof(embeddingOrchestrator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _ownerContext = ownerContext;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Current ambient owner scope (null = unscoped/shared).</summary>
    private MemoryScope? CurrentScope =>
        string.IsNullOrEmpty(_ownerContext?.UserId) ? null : MemoryScope.For(_ownerContext!.UserId!);

    /// <inheritdoc/>
    public async Task<MemoryQueryResult> SearchMemoryAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return MemoryQueryResult.Failed("Query is required for search_memory.");

        return await ExecuteAsync("search_memory", query, async () =>
        {
            var scope = CurrentScope;
            var embedding = await _embeddingOrchestrator.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
            var entities = await _longTerm.SearchEntitiesAsync(embedding, scope: scope, cancellationToken: cancellationToken).ConfigureAwait(false);
            var facts = await _longTerm.SearchFactsAsync(embedding, scope: scope, cancellationToken: cancellationToken).ConfigureAwait(false);
            var preferences = await _longTerm.SearchPreferencesAsync(embedding, scope: scope, cancellationToken: cancellationToken).ConfigureAwait(false);

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
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MemoryQueryResult> RememberPreferenceAsync(
        string preferenceText, string category = "general", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preferenceText))
            return MemoryQueryResult.Failed("preferenceText is required for remember_preference.");

        return await ExecuteAsync("remember_preference", null, async () =>
        {
            var resolvedCategory = string.IsNullOrWhiteSpace(category) ? "general" : category;
            var preference = new Preference
            {
                PreferenceId = _idGenerator.GenerateId(),
                Category = resolvedCategory,
                PreferenceText = preferenceText,
                Confidence = 1.0,
                OwnerId = _ownerContext?.UserId,
                CreatedAtUtc = _clock.UtcNow,
            };
            await _longTerm.AddPreferenceAsync(preference, cancellationToken).ConfigureAwait(false);
            return $"Preference stored: [{resolvedCategory}] {preferenceText}";
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MemoryQueryResult> RememberFactAsync(
        string subject, string predicate, string @object, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return MemoryQueryResult.Failed("subject is required for remember_fact.");

        return await ExecuteAsync("remember_fact", null, async () =>
        {
            var fact = new Fact
            {
                FactId = _idGenerator.GenerateId(),
                Subject = subject,
                Predicate = predicate,
                Object = @object,
                Confidence = 1.0,
                OwnerId = _ownerContext?.UserId,
                CreatedAtUtc = _clock.UtcNow,
            };
            await _longTerm.AddFactAsync(fact, cancellationToken).ConfigureAwait(false);
            return $"Fact stored: {subject} {predicate} {@object}";
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MemoryQueryResult> RecallPreferencesAsync(
        string? category, string? query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(query))
            return MemoryQueryResult.Failed("Either category or query is required for recall_preferences.");

        return await ExecuteAsync("recall_preferences", query, async () =>
        {
            var scope = CurrentScope;
            IReadOnlyList<Preference> preferences;
            if (!string.IsNullOrWhiteSpace(category))
            {
                preferences = await _longTerm.GetPreferencesByCategoryAsync(category, scope, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var embedding = await _embeddingOrchestrator.EmbedQueryAsync(query!, cancellationToken).ConfigureAwait(false);
                preferences = await _longTerm.SearchPreferencesAsync(embedding, scope: scope, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (preferences.Count == 0) return "No preferences found.";
            var sb = new StringBuilder();
            foreach (var p in preferences) sb.AppendLine($"[{p.Category}] {p.PreferenceText}");
            return sb.ToString().Trim();
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MemoryQueryResult> SearchKnowledgeAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return MemoryQueryResult.Failed("Query is required for search_knowledge.");

        return await ExecuteAsync("search_knowledge", query, async () =>
        {
            var embedding = await _embeddingOrchestrator.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
            var entities = await _longTerm.SearchEntitiesAsync(embedding, scope: CurrentScope, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (entities.Count == 0) return "No entities found.";
            var sb = new StringBuilder();
            foreach (var e in entities) sb.AppendLine($"[{e.Type}] {e.Name}: {e.Description}");
            return sb.ToString().Trim();
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<MemoryQueryResult> FindSimilarTasksAsync(string taskDescription, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskDescription))
            return MemoryQueryResult.Failed("taskDescription is required for find_similar_tasks.");

        return await ExecuteAsync("find_similar_tasks", taskDescription, async () =>
        {
            var embedding = await _embeddingOrchestrator.EmbedQueryAsync(taskDescription, cancellationToken).ConfigureAwait(false);
            var traces = await _reasoning.SearchSimilarTracesAsync(embedding, scope: CurrentScope, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (traces.Count == 0) return "No similar tasks found.";
            var sb = new StringBuilder();
            foreach (var t in traces) sb.AppendLine($"[{(t.Success == true ? "✓" : "✗")}] {t.Task}: {t.Outcome}");
            return sb.ToString().Trim();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared execution wrapper: runs <paramref name="operation"/>, wrapping its text in a successful
    /// result, propagating cancellation, and logging + failing on any other exception.
    /// </summary>
    private async Task<MemoryQueryResult> ExecuteAsync(
        string operationName,
        string? query,
        Func<Task<string>> operation)
    {
        try
        {
            return MemoryQueryResult.Ok(await operation().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Honor cancellation - never mask it as a failed result.
            throw;
        }
        catch (Exception ex)
        {
            if (query is null)
                _logger.LogWarning(ex, "{Operation} failed.", operationName);
            else
                _logger.LogWarning(ex, "{Operation} failed for query: {Query}", operationName, query);
            return MemoryQueryResult.Failed(ex.Message);
        }
    }
}
