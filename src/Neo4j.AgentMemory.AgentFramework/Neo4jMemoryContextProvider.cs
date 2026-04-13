using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework.Mapping;

namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// MAF context provider that injects relevant memory into the agent's context before each run.
/// </summary>
public sealed class Neo4jMemoryContextProvider : AIContextProvider
{
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ContextFormatOptions _formatOptions;
    private readonly AgentFrameworkOptions _agentOptions;
    private readonly ILogger<Neo4jMemoryContextProvider> _logger;

    public Neo4jMemoryContextProvider(
        IMemoryService memoryService,
        IEmbeddingProvider embeddingProvider,
        IOptions<ContextFormatOptions> formatOptions,
        IOptions<AgentFrameworkOptions> agentOptions,
        ILogger<Neo4jMemoryContextProvider> logger)
        : base(null, null, null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _formatOptions = formatOptions?.Value ?? new ContextFormatOptions();
        _agentOptions = agentOptions?.Value ?? new AgentFrameworkOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var messages = context.AIContext?.Messages ?? Enumerable.Empty<ChatMessage>();
        var (sessionId, conversationId) = ExtractSessionIds(context);
        return await BuildContextAsync(messages, sessionId, conversationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Core context-building logic, exposed internally for unit testing.
    /// </summary>
    internal async Task<AIContext> BuildContextAsync(
        IEnumerable<ChatMessage> messages,
        string sessionId,
        string conversationId,
        CancellationToken cancellationToken)
    {
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
                queryEmbedding = await _embeddingProvider
                    .GenerateEmbeddingAsync(queryText, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate query embedding; proceeding without semantic search.");
            }

            var recallRequest = new RecallRequest
            {
                SessionId = sessionId,
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
        var (sessionId, conversationId) = ExtractSessionIds(context);

        await PerformStoreAsync(responseMessages, sessionId, conversationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Internal helper exposed for unit testing.</summary>
    internal async Task PerformStoreAsync(
        IEnumerable<ChatMessage> responseMessages,
        string sessionId,
        string conversationId,
        CancellationToken cancellationToken)
    {
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
                            SessionId = sessionId
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Extraction failed for session {SessionId}; messages were persisted.", sessionId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist messages after run for session {SessionId}.", sessionId);
        }
    }

    private (string sessionId, string conversationId) ExtractSessionIds(InvokingContext context)
        => ExtractIds(context.Session, context.Agent);

    private (string sessionId, string conversationId) ExtractSessionIds(InvokedContext context)
        => ExtractIds(context.Session, context.Agent);

    private (string sessionId, string conversationId) ExtractIds(
        AgentSession? session,
        AIAgent? agent)
    {
        string? sessionId = null;
        string? conversationId = null;

        try
        {
            var bag = session?.StateBag;
            if (bag is not null)
            {
                bag.TryGetValue(_agentOptions.DefaultSessionIdHeader, out sessionId,
                    System.Text.Json.JsonSerializerOptions.Default);
                bag.TryGetValue(_agentOptions.DefaultConversationIdHeader, out conversationId,
                    System.Text.Json.JsonSerializerOptions.Default);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract session IDs from state bag.");
        }

        sessionId ??= agent?.Id ?? Guid.NewGuid().ToString("N");
        conversationId ??= Guid.NewGuid().ToString("N");

        return (sessionId, conversationId);
    }
}
