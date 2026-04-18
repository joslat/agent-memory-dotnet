using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework.Mapping;

namespace Neo4j.AgentMemory.AgentFramework;

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
    private readonly ILogger<Neo4jChatHistoryProvider> _logger;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } =
        new[] { nameof(Neo4jChatHistoryProvider) };

    public Neo4jChatHistoryProvider(
        IMemoryService memoryService,
        IClock clock,
        IIdGenerator idGenerator,
        AgentFrameworkOptions options,
        ILogger<Neo4jChatHistoryProvider> logger)
        : base(null, null, null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
        var (sessionId, _) = ExtractIds(context.Session, context.Agent);
        try
        {
            var recallResult = await _memoryService.RecallAsync(
                new Abstractions.Domain.RecallRequest
                {
                    SessionId = sessionId,
                    Query = string.Empty,
                    Options = new RecallOptions
                    {
                        MaxRecentMessages = _options.ContextFormat.MaxContextMessages
                    }
                }, cancellationToken).ConfigureAwait(false);

            return recallResult.Context.RecentMessages.Items
                .Select(MafTypeMapper.ToChatMessage)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to retrieve chat history for session {SessionId}.", sessionId);
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
        var (sessionId, conversationId) = ExtractIds(context.Session, context.Agent);
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
                "Failed to store chat history for session {SessionId}.", sessionId);
        }
    }

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
                bag.TryGetValue(_options.DefaultSessionIdHeader, out sessionId,
                    System.Text.Json.JsonSerializerOptions.Default);
                bag.TryGetValue(_options.DefaultConversationIdHeader, out conversationId,
                    System.Text.Json.JsonSerializerOptions.Default);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract session IDs from state bag.");
        }

        sessionId ??= agent?.Id ?? Guid.NewGuid().ToString("N");
        // Fall back to sessionId (not a new GUID) to preserve cross-turn correlation.
        conversationId ??= sessionId;

        return (sessionId, conversationId);
    }
}
