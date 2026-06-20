using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Mapping;

namespace AgentMemory.AgentFramework;

/// <summary>
/// A MAF 1.1.0-compatible <see cref="ChatHistoryProvider"/> that stores and retrieves
/// conversation history from Neo4j via <see cref="IMemoryService"/>.
/// </summary>
/// <remarks>
/// Register this in DI as a <see cref="ChatHistoryProvider"/> and set it on
/// <c>ChatClientAgentOptions.ChatHistoryProvider</c>.  For each agent turn it
/// retrieves recent messages from Neo4j (pre-run) and persists the new request +
/// response messages back (post-run).
/// </remarks>
public sealed class Neo4jChatHistoryProvider : ChatHistoryProvider
{
    private readonly IMemoryService _memoryService;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly AgentFrameworkOptions _options;
    private readonly IMemoryStoreContext? _storeContext;
    private readonly IWritableMemoryOwnerContext? _ownerContext;
    private readonly ILogger<Neo4jChatHistoryProvider> _logger;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } =
        new[] { nameof(Neo4jChatHistoryProvider) };

    public Neo4jChatHistoryProvider(
        IMemoryService memoryService,
        IClock clock,
        IIdGenerator idGenerator,
        AgentFrameworkOptions options,
        ILogger<Neo4jChatHistoryProvider> logger,
        IMemoryStoreContext? storeContext = null,
        IWritableMemoryOwnerContext? ownerContext = null)
        : base(null, null, null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _storeContext = storeContext;
        _ownerContext = ownerContext;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Called before each agent turn. Retrieves recent messages from Neo4j and
    /// returns them so they are prepended to the agent's request context.
    /// </summary>
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var ids = ExtractIds(context.Session, context.Agent);
        ApplyStoreContext(ids.applicationId);
        ApplyOwnerContext(ids.userId);
        try
        {
            var recallResult = await _memoryService.RecallAsync(
                new Abstractions.Domain.RecallRequest
                {
                    SessionId = ids.sessionId,
                    UserId = ids.userId,
                    Query = string.Empty,
                    Options = new RecallOptions
                    {
                        MaxRecentMessages = _options.ContextFormat.MaxContextMessages
                    }
                }, cancellationToken).ConfigureAwait(false);

            // RecentMessages is newest-first (the recall query orders DESC); a chat-history provider must
            // hand the agent its prior turns in chronological (oldest-first) order, so reverse here.
            return recallResult.Context.RecentMessages.Items
                .Reverse()
                .Select(MafTypeMapper.ToChatMessage)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to retrieve chat history for session {SessionId}.", ids.sessionId);
            return [];
        }
    }

    /// <summary>
    /// Called after each successful agent turn. Persists both the accumulated
    /// request messages and the response messages into Neo4j memory.
    /// </summary>
    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var (sessionId, conversationId, userId, applicationId) = ExtractIds(context.Session, context.Agent);
        ApplyStoreContext(applicationId);
        ApplyOwnerContext(userId);
        try
        {
            // Persist request messages (user + system turns not already in memory)
            foreach (var msg in context.RequestMessages)
            {
                if (string.IsNullOrWhiteSpace(msg.Text)) continue;
                var message = MafTypeMapper.ToInternalMessage(
                    msg, sessionId, conversationId, _clock, _idGenerator);
                await _memoryService
                    .AddMessageAsync(
                        message.SessionId, message.ConversationId,
                        message.Role, message.Content, message.Metadata,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Persist response messages
            var storedResponses = new List<Abstractions.Domain.Message>();
            foreach (var msg in context.ResponseMessages ?? [])
            {
                if (string.IsNullOrWhiteSpace(msg.Text)) continue;
                var stored = await _memoryService
                    .AddMessageAsync(
                        sessionId, conversationId,
                        MafTypeMapper.ToInternalRole(msg.Role),
                        msg.Text,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                storedResponses.Add(stored);
            }

            // Optionally trigger knowledge extraction on the persisted responses
            if (_options.AutoExtractOnPersist && storedResponses.Count > 0)
            {
                try
                {
                    await _memoryService.ExtractAndPersistAsync(
                        new Abstractions.Domain.ExtractionRequest
                        {
                            Messages = storedResponses,
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
                "Failed to store chat history for session {SessionId}.", sessionId);
        }
    }

    private (string sessionId, string conversationId, string? userId, string? applicationId) ExtractIds(
        AgentSession? session,
        AIAgent? agent)
    {
        string? sessionId = null;
        string? conversationId = null;
        string? userId = null;
        string? applicationId = null;

        try
        {
            var bag = session?.StateBag;
            if (bag is not null)
            {
                bag.TryGetValue(_options.DefaultSessionIdKey, out sessionId,
                    System.Text.Json.JsonSerializerOptions.Default);
                bag.TryGetValue(_options.DefaultConversationIdKey, out conversationId,
                    System.Text.Json.JsonSerializerOptions.Default);
                bag.TryGetValue(_options.DefaultUserIdKey, out userId,
                    System.Text.Json.JsonSerializerOptions.Default);
                bag.TryGetValue(_options.DefaultApplicationIdKey, out applicationId,
                    System.Text.Json.JsonSerializerOptions.Default);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract identity from state bag.");
        }

        sessionId ??= agent?.Id ?? Guid.NewGuid().ToString("N");
        // Fall back to sessionId (not a new GUID) to preserve cross-turn correlation.
        conversationId ??= sessionId;

        return (sessionId, conversationId,
            string.IsNullOrWhiteSpace(userId) ? null : userId,
            string.IsNullOrWhiteSpace(applicationId) ? null : applicationId);
    }

    // Routes the memory store for this scope when an application_id is supplied and a writable store
    // context is registered (R1b). No-op otherwise.
    private void ApplyStoreContext(string? applicationId)
    {
        if (applicationId is not null && _storeContext is IWritableMemoryStoreContext writable)
            writable.ApplicationId = applicationId;
    }

    // Pushes the turn's owner (IC8) into the ambient context so the LLM-invokable facade tools scope by
    // owner without trusting the model. Set unconditionally (incl. null = shared) so a previous turn's
    // owner can't bleed through. NOTE: the default owner context is AsyncLocal-backed and a value set in
    // this awaited hook does not flow back to the framework caller; for guaranteed scoping the host must
    // set the owner context around the run (or register a scoped context). See
    // docs/reviews/review-2026-06-13-cycle3.md (finding #4).
    private void ApplyOwnerContext(string? userId)
    {
        if (_ownerContext is not null)
            _ownerContext.UserId = userId;
    }
}
