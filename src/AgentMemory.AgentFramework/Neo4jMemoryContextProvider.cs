using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.AgentFramework.Tools;

namespace AgentMemory.AgentFramework;

/// <summary>
/// MAF context provider that injects relevant memory into the agent's context before each run.
/// </summary>
public sealed class Neo4jMemoryContextProvider : AIContextProvider
{
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly RecallOptions _recallOptions;
    private readonly ContextFormatOptions _formatOptions;
    private readonly AgentFrameworkOptions _agentOptions;
    private readonly IMemoryStoreContext? _storeContext;
    private readonly IWritableMemoryOwnerContext? _ownerContext;
    private readonly MemoryToolFactory? _toolFactory;
    private readonly ILogger<Neo4jMemoryContextProvider> _logger;

    public Neo4jMemoryContextProvider(
        IMemoryService memoryService,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IClock clock,
        IIdGenerator idGenerator,
        IOptions<MemoryOptions> memoryOptions,
        IOptions<ContextFormatOptions> formatOptions,
        IOptions<AgentFrameworkOptions> agentOptions,
        ILogger<Neo4jMemoryContextProvider> logger,
        IMemoryStoreContext? storeContext = null,
        IWritableMemoryOwnerContext? ownerContext = null,
        MemoryToolFactory? toolFactory = null)
        // AIContextProvider(IServiceProvider? sp, ILogger? logger, string? stateKey)
        // All three are passed as null: we supply our own ILogger via constructor injection,
        // we don't need the base-class IServiceProvider, and StateKey is exposed as our own property.
        : base(null, null, null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _embeddingOrchestrator = embeddingOrchestrator ?? throw new ArgumentNullException(nameof(embeddingOrchestrator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _recallOptions = memoryOptions?.Value.Recall ?? RecallOptions.Default;
        _formatOptions = formatOptions?.Value ?? new ContextFormatOptions();
        _agentOptions = agentOptions?.Value ?? new AgentFrameworkOptions();
        _storeContext = storeContext;
        _ownerContext = ownerContext;
        _toolFactory = toolFactory;
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
                return BuildResult();

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
                QueryEmbedding = queryEmbedding,
                // Retrieval tuning (limits, MinSimilarityScore, BlendMode, Intent) comes from the
                // configured RecallOptions (#87) -- but Scope is explicitly cleared: scope must always be
                // resolved from this invocation's authenticated userId (via #100's isolation policy),
                // never from a statically configured value. A host who ever sets
                // MemoryOptions.Recall.Scope globally must not have it silently override the real,
                // per-invocation owner here.
                Options = _recallOptions with { Scope = null }
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
                return BuildResult();
            }

            var contextMessages = MafTypeMapper.ToContextMessages(recallResult.Context, _formatOptions);

            if (contextMessages.Count == 0)
                return BuildResult();

            return BuildResult(contextMessages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error in Neo4jMemoryContextProvider for session {SessionId}.", sessionId);
            return BuildResult();
        }
    }

    /// <summary>
    /// Builds the <see cref="AIContext"/> returned by every branch of <see cref="BuildContextAsync"/>, so
    /// tool exposure is consistent regardless of whether this turn had recall hits, no user messages, or
    /// a recall failure -- a turn with nothing to recall must not silently lose tool availability.
    /// </summary>
    private AIContext BuildResult(IReadOnlyList<ChatMessage>? messages = null) => new()
    {
        Messages = messages,
        Tools = _agentOptions.ExposeMemoryToolsFromContextProvider
            ? _toolFactory?.CreateAIFunctions()
            : null,
    };

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

        var requestMessages = context.RequestMessages ?? Enumerable.Empty<ChatMessage>();
        var responseMessages = context.ResponseMessages ?? Enumerable.Empty<ChatMessage>();
        var ids = ExtractIds(context.Session, context.Agent);
        using var storeScope = ApplyStoreContext(ids.applicationId);

        await PerformStoreAsync(requestMessages, responseMessages, ids.sessionId, ids.conversationId, cancellationToken, ids.userId)
            .ConfigureAwait(false);
    }

    /// <summary>Internal helper exposed for unit testing.</summary>
    internal async Task PerformStoreAsync(
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage> responseMessages,
        string sessionId,
        string conversationId,
        CancellationToken cancellationToken,
        string? userId = null)
    {
        using var ownerScope = _ownerContext?.BeginOwnerScope(userId);
        try
        {
            // Response messages are persisted as new :Message nodes, as before. Request messages are
            // deliberately NOT persisted here (#89): ChatMessage.MessageId (used below for response-side
            // dedup) is essentially never populated on caller-constructed request messages, so it can't
            // help there -- request-message persistence ownership intentionally stays solely with
            // Neo4jChatHistoryProvider. Building transient (never-persisted) Message objects for extraction
            // only captures what the user said without risking a duplicate node. Filtered to ChatRole.User
            // -- the same filter recall already applies (BuildContextAsync above) -- so a system prompt or
            // other non-user content accumulated in RequestMessages doesn't get minted into spurious
            // entities/facts/preferences every turn.
            var transientRequestMessages = requestMessages
                .Where(msg => msg.Role == ChatRole.User && !string.IsNullOrWhiteSpace(msg.Text))
                .Select(msg => MafTypeMapper.ToInternalMessage(msg, sessionId, conversationId, _clock, _idGenerator))
                .ToList();

            var storedMessages = new List<Message>();
            foreach (var msg in responseMessages)
            {
                if (string.IsNullOrWhiteSpace(msg.Text)) continue;

                // When the underlying IChatClient stamps a provider-native MessageId on this response
                // message, persist it under a deterministic id (#89): if another persisting component
                // (e.g. Neo4jChatHistoryProvider, Neo4jChatMessageStore) configured on the same agent
                // observes the same response, it converges on this same :Message node instead of creating
                // a duplicate. Falls back to today's fresh-id behavior when absent (no regression).
                var providerId = MafTypeMapper.TryGetProviderMessageId(msg);
                var stored = providerId is not null
                    ? await _memoryService
                        .AddMessageWithIdAsync(
                            sessionId, conversationId,
                            MafTypeMapper.ToInternalRole(msg.Role),
                            msg.Text, providerId,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false)
                    : await _memoryService
                        .AddMessageAsync(
                            sessionId, conversationId,
                            MafTypeMapper.ToInternalRole(msg.Role),
                            msg.Text,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                storedMessages.Add(stored);
            }

            // Extraction sees the complete turn (#89): a fact or preference the user states in their
            // request is now captured even if the assistant never repeats it back. Because
            // transientRequestMessages were never persisted as :Message nodes above, provenance for
            // anything extracted from them is incomplete: the EXTRACTED_FROM edge silently fails to
            // attach (the MATCH in PersistenceStage's linking Cypher finds no such node), AND the
            // extracted node's own source_message_ids property will still list the transient (never
            // persisted) message id, i.e. a dangling reference, not just a missing edge. The extracted
            // fact/preference itself is still created and recallable correctly; only its link back to the
            // literal source message is best-effort, not guaranteed, unless another component also
            // persisted that exact message.
            var turnMessages = transientRequestMessages.Concat(storedMessages).ToList();
            if (_agentOptions.AutoExtractOnPersist && turnMessages.Count > 0)
            {
                try
                {
                    await _memoryService.ExtractAndPersistAsync(
                        new ExtractionRequest
                        {
                            Messages = turnMessages,
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
