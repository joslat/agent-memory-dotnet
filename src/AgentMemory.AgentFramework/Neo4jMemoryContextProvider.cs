using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Mapping;

namespace AgentMemory.AgentFramework;

/// <summary>
/// MAF context provider that injects relevant memory into the agent's context before each run.
/// </summary>
public sealed class Neo4jMemoryContextProvider : AIContextProvider
{
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly ContextFormatOptions _formatOptions;
    private readonly AgentFrameworkOptions _agentOptions;
    private readonly IMemoryStoreContext? _storeContext;
    private readonly IWritableMemoryOwnerContext? _ownerContext;
    private readonly ILogger<Neo4jMemoryContextProvider> _logger;

    public Neo4jMemoryContextProvider(
        IMemoryService memoryService,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IOptions<ContextFormatOptions> formatOptions,
        IOptions<AgentFrameworkOptions> agentOptions,
        ILogger<Neo4jMemoryContextProvider> logger,
        IMemoryStoreContext? storeContext = null,
        IWritableMemoryOwnerContext? ownerContext = null)
        // AIContextProvider(IServiceProvider? sp, ILogger? logger, string? stateKey)
        // All three are passed as null: we supply our own ILogger via constructor injection,
        // we don't need the base-class IServiceProvider, and StateKey is exposed as our own property.
        : base(null, null, null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _embeddingOrchestrator = embeddingOrchestrator ?? throw new ArgumentNullException(nameof(embeddingOrchestrator));
        _formatOptions = formatOptions?.Value ?? new ContextFormatOptions();
        _agentOptions = agentOptions?.Value ?? new AgentFrameworkOptions();
        _storeContext = storeContext;
        _ownerContext = ownerContext;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Identifies this provider in the MAF pipeline for introspection.</summary>
    public string StateKey => "Neo4jMemory";

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var messages = context.AIContext?.Messages ?? Enumerable.Empty<ChatMessage>();
        var ids = ExtractIds(context.Session, context.Agent);
        using var storeScope = ApplyStoreContext(ids.applicationId);
        return await BuildContextAsync(messages, ids.sessionId, ids.conversationId, cancellationToken, ids.userId)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Core context-building logic, exposed internally for unit testing.
    /// </summary>
    internal async Task<AIContext> BuildContextAsync(
        IEnumerable<ChatMessage> messages,
        string sessionId,
        string conversationId,
        CancellationToken cancellationToken,
        string? userId = null)
    {
        // Set the ambient owner BEFORE recall so the LLM-invokable facade tools the agent calls mid-turn
        // (search_memory / remember_* etc.) scope to this owner instead of running unscoped. Scoped (not a
        // bare assignment) so the value is restored once this hook returns rather than leaking into
        // whatever runs next on this ambient context. NOTE: this hook still can't, on its own, guarantee
        // the value survives into the tool-calling loop that runs AFTER it returns -- see
        // MemoryOwnerScopingAgent (#90), which wraps the complete invocation for that guarantee.
        using var ownerScope = _ownerContext?.BeginOwnerScope(userId);
        try
        {
            var userMessages = messages
                .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
                .ToList();

            if (userMessages.Count == 0)
                return new AIContext();

            var queryText = string.Join("\n", userMessages.Select(m => m.Text));

            float[]? queryEmbedding = null;
            try
            {
                queryEmbedding = await _embeddingOrchestrator
                    .EmbedQueryAsync(queryText, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate query embedding; proceeding without semantic search.");
            }

            var recallRequest = new RecallRequest
            {
                SessionId = sessionId,
                UserId = userId,
                Query = queryText,
                QueryEmbedding = queryEmbedding
            };

            RecallResult recallResult;
            try
            {
                recallResult = await _memoryService
                    .RecallAsync(recallRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory recall failed for session {SessionId}; returning empty context.", sessionId);
                return new AIContext();
            }

            var contextMessages = MafTypeMapper.ToContextMessages(recallResult.Context, _formatOptions);

            if (contextMessages.Count == 0)
                return new AIContext();

            return new AIContext { Messages = contextMessages };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error in Neo4jMemoryContextProvider for session {SessionId}.", sessionId);
            return new AIContext();
        }
    }

    /// <summary>
    /// Post-run hook: persists response messages and optionally triggers extraction.
    /// Skipped if the invocation raised an exception. Failures are logged but never re-thrown.
    /// </summary>
    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
        {
            _logger.LogDebug("Skipping memory persistence: invocation failed with exception.");
            return;
        }

        var responseMessages = context.ResponseMessages ?? Enumerable.Empty<ChatMessage>();
        var ids = ExtractIds(context.Session, context.Agent);
        using var storeScope = ApplyStoreContext(ids.applicationId);

        await PerformStoreAsync(responseMessages, ids.sessionId, ids.conversationId, cancellationToken, ids.userId)
            .ConfigureAwait(false);
    }

    /// <summary>Internal helper exposed for unit testing.</summary>
    internal async Task PerformStoreAsync(
        IEnumerable<ChatMessage> responseMessages,
        string sessionId,
        string conversationId,
        CancellationToken cancellationToken,
        string? userId = null)
    {
        using var ownerScope = _ownerContext?.BeginOwnerScope(userId);
        try
        {
            var storedMessages = new List<Message>();
            foreach (var msg in responseMessages)
            {
                if (string.IsNullOrWhiteSpace(msg.Text)) continue;

                var stored = await _memoryService
                    .AddMessageAsync(
                        sessionId, conversationId,
                        MafTypeMapper.ToInternalRole(msg.Role),
                        msg.Text,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                storedMessages.Add(stored);
            }

            if (_agentOptions.AutoExtractOnPersist && storedMessages.Count > 0)
            {
                try
                {
                    await _memoryService.ExtractAndPersistAsync(
                        new ExtractionRequest
                        {
                            Messages = storedMessages,
                            SessionId = sessionId,
                            UserId = userId
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Extraction failed for session {SessionId}; messages were persisted.", sessionId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist messages after run for session {SessionId}.", sessionId);
        }
    }

    private (string sessionId, string conversationId, string? userId, string? applicationId) ExtractIds(
        AgentSession? session,
        AIAgent? agent)
    {
        MemoryIdentity identity;
        try
        {
            identity = session.GetMemoryIdentity(_agentOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract identity from state bag.");
            identity = default;
        }

        var sessionId = identity.SessionId ?? agent?.Id ?? Guid.NewGuid().ToString("N");
        // P2-4: Fall back to sessionId (not a new GUID) to preserve cross-turn correlation.
        var conversationId = identity.ConversationId ?? sessionId;

        return (sessionId, conversationId, identity.UserId, identity.ApplicationId);
    }

    // Routes the memory store for this scope when an application_id is supplied and a writable store
    // context is registered (R1b). No-op otherwise. Scoped (not a bare assignment) so the value is
    // restored once this hook returns. Mutating a singleton context is only safe for one application per
    // host; register a scoped IMemoryStoreContext to route per request, or use MemoryOwnerScopingAgent
    // (#90) to guarantee the scope spans the complete invocation including the tool-calling loop.
    private IDisposable? ApplyStoreContext(string? applicationId) =>
        applicationId is not null && _storeContext is IWritableMemoryStoreContext writable
            ? writable.BeginStoreScope(applicationId)
            : null;
}
