using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Facade service for all memory operations.
/// </summary>
public sealed class MemoryService : IMemoryService
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly IMemoryContextAssembler _assembler;
    private readonly IMemoryExtractionPipeline _extraction;
    private readonly MemoryOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(
        IShortTermMemoryService shortTerm,
        IMemoryContextAssembler assembler,
        IMemoryExtractionPipeline extraction,
        IOptions<MemoryOptions> options,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<MemoryService> logger)
    {
        _shortTerm = shortTerm;
        _assembler = assembler;
        _extraction = extraction;
        _options = options.Value;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    public async Task<RecallResult> RecallAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Recalling memory for session {SessionId}", request.SessionId);
        var context = await _assembler.AssembleContextAsync(request, cancellationToken);

        int totalItems = context.RecentMessages.Items.Count
            + context.RelevantMessages.Items.Count
            + context.RelevantEntities.Items.Count
            + context.RelevantPreferences.Items.Count
            + context.RelevantFacts.Items.Count
            + context.SimilarTraces.Items.Count;

        int estimatedChars =
            context.RecentMessages.Items.Sum(m => m.Content.Length)
            + context.RelevantMessages.Items.Sum(m => m.Content.Length)
            + context.RelevantEntities.Items.Sum(e => (e.Name?.Length ?? 0) + (e.Description?.Length ?? 0))
            + context.RelevantPreferences.Items.Sum(p => p.PreferenceText.Length)
            + context.RelevantFacts.Items.Sum(f => f.Subject.Length + f.Predicate.Length + f.Object.Length)
            + context.SimilarTraces.Items.Sum(t => t.Task.Length)
            + (context.GraphRagContext?.Length ?? 0);

        var budget = _options.ContextBudget;
        int? estimatedTokens = null;
        if (budget.MaxTokens.HasValue)
            estimatedTokens = estimatedChars / 4;

        return new RecallResult
        {
            Context = context,
            TotalItemsRetrieved = totalItems,
            EstimatedTokenCount = estimatedTokens
        };
    }

    public async Task<Message> AddMessageAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var message = new Message
        {
            MessageId = _idGenerator.GenerateId(),
            SessionId = sessionId,
            ConversationId = conversationId,
            Role = role,
            Content = content,
            TimestampUtc = _clock.UtcNow,
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        return await _shortTerm.AddMessageAsync(message, cancellationToken);
    }

    public Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default)
    {
        return _shortTerm.AddMessagesAsync(messages, cancellationToken);
    }

    public Task<ExtractionResult> ExtractAndPersistAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Extracting and persisting memory for session {SessionId}", request.SessionId);
        return _extraction.ExtractAsync(request, cancellationToken);
    }

    public Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Clearing session {SessionId}", sessionId);
        return _shortTerm.ClearSessionAsync(sessionId, cancellationToken);
    }
}
