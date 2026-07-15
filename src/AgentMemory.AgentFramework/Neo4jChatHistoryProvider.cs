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
        using var storeScope = ApplyStoreContext(ids.applicationId);
        using var ownerScope = ApplyOwnerContext(ids.userId);
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
        using var storeScope = ApplyStoreContext(applicationId);
        await PerformStoreAsync(
            context.RequestMessages, context.ResponseMessages ?? [], sessionId, conversationId, cancellationToken, userId)
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
        using var ownerScope = ApplyOwnerContext(userId);
        try
        {
            // Persist request messages (user + system turns not already in memory). Both request and
            // response messages are genuinely persisted here, so -- unlike Neo4jMemoryContextProvider,
            // which deliberately does NOT persist request messages to avoid duplicating what this
            // provider already stores -- extraction can safely see both with full provenance (#89).
            var storedRequests = new List<Abstractions.Domain.Message>();
            foreach (var msg in requestMessages)
            {
                if (string.IsNullOrWhiteSpace(msg.Text)) continue;
                var message = MafTypeMapper.ToInternalMessage(
                    msg, sessionId, conversationId, _clock, _idGenerator);
                var stored = await _memoryService
                    .AddMessageAsync(
                        message.SessionId, message.ConversationId,
                        message.Role, message.Content, message.Metadata,
                        cancellationToken)
                    .ConfigureAwait(false);
                storedRequests.Add(stored);
            }

            // Persist response messages
            var storedResponses = new List<Abstractions.Domain.Message>();
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
                storedResponses.Add(stored);
            }

            // Optionally trigger knowledge extraction on the complete persisted turn (#89): a fact or
            // preference the user states in their request is now captured even if the assistant never
            // repeats it back. Extraction input is filtered to user-role request messages -- persistence
            // above still stores every role (unchanged), but a system prompt or other non-user content
            // in RequestMessages shouldn't be minted into spurious entities/facts/preferences every turn.
            var turnMessages = storedRequests.Where(m => m.Role == "user").Concat(storedResponses).ToList();
            if (_options.AutoExtractOnPersist && turnMessages.Count > 0)
            {
                try
                {
                    await _memoryService.ExtractAndPersistAsync(
                        new Abstractions.Domain.ExtractionRequest
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
                "Failed to store chat history for session {SessionId}.", sessionId);
        }
    }

    private (string sessionId, string conversationId, string? userId, string? applicationId) ExtractIds(
        AgentSession? session,
        AIAgent? agent)
    {
        MemoryIdentity identity;
        try
        {
            identity = session.GetMemoryIdentity(_options);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract identity from state bag.");
            identity = default;
        }

        var sessionId = identity.SessionId ?? agent?.Id ?? Guid.NewGuid().ToString("N");
        // Fall back to sessionId (not a new GUID) to preserve cross-turn correlation.
        var conversationId = identity.ConversationId ?? sessionId;

        return (sessionId, conversationId, identity.UserId, identity.ApplicationId);
    }

    // Routes the memory store for this scope when an application_id is supplied and a writable store
    // context is registered (R1b). No-op otherwise. Scoped (not a bare assignment) so the value is
    // restored once this hook returns. See MemoryOwnerScopingAgent (#90) for a guarantee spanning the
    // complete invocation including the tool-calling loop.
    private IDisposable? ApplyStoreContext(string? applicationId) =>
        applicationId is not null && _storeContext is IWritableMemoryStoreContext writable
            ? writable.BeginStoreScope(applicationId)
            : null;

    // Pushes the turn's owner (IC8) into the ambient context so the LLM-invokable facade tools scope by
    // owner without trusting the model. Scoped (not a bare assignment) so the value is restored once this
    // hook returns rather than leaking into whatever runs next. NOTE: the default owner context is
    // AsyncLocal-backed and a value set in this awaited hook does not flow back to the framework caller,
    // so it still can't guarantee the value survives into the tool-calling loop on its own -- see
    // MemoryOwnerScopingAgent (#90), which wraps the complete invocation for that guarantee. See also
    // docs/reviews/review-2026-06-13-cycle3.md (finding #4).
    private IDisposable? ApplyOwnerContext(string? userId) => _ownerContext?.BeginOwnerScope(userId);
}
