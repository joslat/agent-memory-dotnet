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

    private (string sessionId, string conversationId) ExtractSessionIds(InvokingContext context)
    {
        string? sessionId = null;
        string? conversationId = null;

        try
        {
            var bag = context.Session?.StateBag;
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

        sessionId ??= context.Agent?.Id ?? Guid.NewGuid().ToString("N");
        conversationId ??= Guid.NewGuid().ToString("N");

        return (sessionId, conversationId);
    }
}
