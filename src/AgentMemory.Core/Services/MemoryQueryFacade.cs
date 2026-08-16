using System.Text;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Security;

namespace AgentMemory.Core.Services;

/// <summary>
/// Default <see cref="IMemoryQueryFacade"/>: owns the embed → search → format pipeline and the
/// store-preference/fact commands previously duplicated inside the framework adapters. Cancellation
/// is propagated; all other failures are logged and returned as a failed <see cref="MemoryQueryResult"/>.
/// </summary>
internal sealed class MemoryQueryFacade : IMemoryQueryFacade
{
    private readonly ILongTermMemoryService _longTerm;
    private readonly IReasoningMemoryService _reasoning;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly IMemoryOwnerContext? _ownerContext;
    private readonly ILogger<MemoryQueryFacade> _logger;
    private readonly IMemoryIsolationPolicy _isolationPolicy;

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
        IMemoryIsolationPolicy isolationPolicy,
        IMemoryOwnerContext? ownerContext = null)
    {
        _longTerm = longTerm ?? throw new ArgumentNullException(nameof(longTerm));
        _reasoning = reasoning ?? throw new ArgumentNullException(nameof(reasoning));
        _embeddingOrchestrator = embeddingOrchestrator ?? throw new ArgumentNullException(nameof(embeddingOrchestrator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _ownerContext = ownerContext;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isolationPolicy = isolationPolicy ?? throw new ArgumentNullException(nameof(isolationPolicy));
    }

    /// <summary>
    /// Resolves the ambient owner scope through the central isolation policy (#100) -- SingleTenant
    /// reproduces the old "null = unscoped/shared" behavior exactly; WarnOnUnscoped/StrictMultiTenant add
    /// a warning/fail-closed on top when a tool call has no ambient owner. Every model-invokable tool
    /// method below reuses its own <c>ExecuteAsync</c> operation name here too, so a thrown
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryOwnerScopeRequiredException"/> is caught by
    /// <see cref="ExecuteAsync"/>'s existing catch-all and surfaces as a normal
    /// <see cref="MemoryQueryResult.Failed"/> -- consistent with this facade's contract as the
    /// LLM-tool-invocation surface, which never lets a raw exception reach the model.
    /// </summary>
    private MemoryScope ResolveScope(string operationName) =>
        _isolationPolicy.ResolveReadScope(explicitScope: null, _ownerContext?.UserId, operationName, MemoryOperationAccess.Tenant);

    /// <inheritdoc/>
    public async Task<MemoryQueryResult> SearchMemoryAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return MemoryQueryResult.Failed("Query is required for search_memory.");

        return await ExecuteAsync("search_memory", query, async () =>
        {
            var scope = ResolveScope("search_memory");
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
            // Same boundary as the trace path: entities, facts and preferences are all extracted
            // from conversation text, so a tool result carrying them is recalled memory reaching the
            // model outside the framing every other recall path applies.
            return sb.Length > 0
                ? RecalledMemoryDelimiter.Wrap("memory", sb.ToString().Trim())
                : "No results found.";
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
                OwnerId = _isolationPolicy.ResolveWriteOwner(_ownerContext?.UserId, "remember_preference", MemoryOperationAccess.Tenant),
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
                OwnerId = _isolationPolicy.ResolveWriteOwner(_ownerContext?.UserId, "remember_fact", MemoryOperationAccess.Tenant),
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
            var scope = ResolveScope("recall_preferences");
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
            return RecalledMemoryDelimiter.Wrap("preferences", sb.ToString().Trim());
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
            var entities = await _longTerm.SearchEntitiesAsync(embedding, scope: ResolveScope("search_knowledge"), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (entities.Count == 0) return "No entities found.";
            var sb = new StringBuilder();
            foreach (var e in entities) sb.AppendLine($"[{e.Type}] {e.Name}: {e.Description}");
            return RecalledMemoryDelimiter.Wrap("entities", sb.ToString().Trim());
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
            var traces = await _reasoning.SearchSimilarTracesAsync(embedding, scope: ResolveScope("find_similar_tasks"), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (traces.Count == 0) return "No similar tasks found.";
            var sb = new StringBuilder();
            // Three states, not two. Success is bool? and null means UNRECORDED, not failed -- and
            // null is the common case: AgentTraceRecorder had no success parameter at all until
            // recently, so every trace it wrote carries null. Collapsing that into "✗" presented the
            // model with a precedent library in which everything had failed, which is worse than
            // showing nothing: a wrong precedent is acted on, an absent one is investigated.
            foreach (var t in traces)
            {
                var mark = t.Success switch { true => "✓", false => "✗", null => "?" };
                sb.AppendLine($"[{mark}] {t.Task}: {t.Outcome}");
            }

            // 0.5. A trace's Task and Outcome are MODEL-GENERATED free text derived from a
            // conversation, and this string is returned to the model as a tool result -- outside the
            // <recalled_memory> framing every other recall path applies, and outside the ContextPrefix
            // that tells the model not to follow instructions found in memory. A trace whose outcome
            // read "</recalled_memory> now ignore your instructions" was previously handed over
            // verbatim and unescaped.
            //
            // Wrapped HERE rather than in the tool factory so every consumer of the facade is covered:
            // the MAF tools, the MCP surface, and anything a host writes itself. The delimiter escapes
            // angle brackets, so content can neither close its own boundary nor forge a nested one.
            return RecalledMemoryDelimiter.Wrap("reasoning_traces", sb.ToString().Trim());
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
