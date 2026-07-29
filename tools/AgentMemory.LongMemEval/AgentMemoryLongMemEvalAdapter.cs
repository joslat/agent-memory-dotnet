using System.Collections.ObjectModel;
using System.Text;
using AgentEval.Core;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Adapts AgentMemory to AgentEval's LongMemEval runner without leaving the injected history in the
/// answer model's context. History is buffered by the synchronous AgentEval capability method, then
/// batch-persisted and semantically recalled before the question is sent to the answer model.
/// </summary>
public sealed class AgentMemoryLongMemEvalAdapter :
    IEvaluableAgent,
    IHistoryInjectableAgent,
    ISessionResettableAgent
{
    internal const string SystemPrompt =
        "Answer the question using only the retrieved memory below. " +
        "Be concise and do not claim information that is absent from memory.";

    private readonly IMemoryService _memory;
    private readonly IChatClient _chatClient;
    private readonly string _runId;
    private readonly LongMemEvalAdapterOptions _options;
    private readonly object _stateLock = new();
    private readonly List<LongMemEvalQuestionTelemetry> _telemetry = [];
    private IReadOnlyList<(string UserMessage, string AssistantResponse)>? _pendingHistory;
    private int _questionNumber;
    private string _sessionId;
    private string _ownerId;

    public AgentMemoryLongMemEvalAdapter(
        IMemoryService memory,
        IChatClient chatClient,
        string runId,
        LongMemEvalAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        _memory = memory;
        _chatClient = chatClient;
        _runId = Sanitize(runId);
        _options = options ?? new LongMemEvalAdapterOptions();
        _sessionId = ScopeId("session", 0);
        _ownerId = ScopeId("owner", 0);
    }

    public string Name => "AgentMemory.LongMemEval";

    public IReadOnlyList<LongMemEvalQuestionTelemetry> QuestionTelemetry
    {
        get
        {
            lock (_stateLock)
                return new ReadOnlyCollection<LongMemEvalQuestionTelemetry>(_telemetry.ToArray());
        }
    }

    public void InjectConversationHistory(
        IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
    {
        ArgumentNullException.ThrowIfNull(conversationTurns);
        var materialized = conversationTurns.ToArray();
        lock (_stateLock)
        {
            if (_pendingHistory is not null)
            {
                throw new InvalidOperationException(
                    "LongMemEval history was injected more than once for the same question.");
            }

            _pendingHistory = materialized;
        }
    }

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            _questionNumber++;
            _sessionId = ScopeId("session", _questionNumber);
            _ownerId = ScopeId("owner", _questionNumber);
            _pendingHistory = null;
        }

        return Task.CompletedTask;
    }

    public async Task<AgentResponse> InvokeAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        IReadOnlyList<(string UserMessage, string AssistantResponse)> history;
        string sessionId;
        string ownerId;
        int questionNumber;
        lock (_stateLock)
        {
            history = _pendingHistory
                ?? throw new InvalidOperationException(
                    "LongMemEval question cannot run before conversation history is injected.");
            if (history.Count == 0)
            {
                throw new InvalidOperationException(
                    "LongMemEval question cannot run with empty conversation history.");
            }

            _pendingHistory = null;
            sessionId = _sessionId;
            ownerId = _ownerId;
            questionNumber = _questionNumber;
        }

        LongMemEvalEvidenceQuestion? evidenceQuestion = null;
        try
        {
            if (_options.EvidenceIndex is not null)
                evidenceQuestion = _options.EvidenceIndex.Resolve(history, prompt);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTelemetry(questionNumber, 0, 0, false, "evidence-resolution-error");
            throw;
        }

        var originsByMessageId = new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal);
        var messages = BuildMessages(
            history, sessionId, ownerId, questionNumber, evidenceQuestion, originsByMessageId);
        try
        {
            _ = await LongMemEvalRuntime.ExecuteStageAsync(
                "storage",
                () => _memory.AddMessagesAsync(messages, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTelemetry(questionNumber, 0, 0, false, "storage-error");
            throw;
        }

        RecallResult recall;
        try
        {
            recall = await LongMemEvalRuntime.ExecuteStageAsync(
                "retrieval",
                () => _memory.RecallAsync(
            new RecallRequest
            {
                SessionId = sessionId,
                UserId = ownerId,
                Query = prompt,
                Options = new RecallOptions
                {
                    MaxRecentMessages = 0,
                    MaxRelevantMessages = _options.MaxRelevantMessages,
                    MaxEntities = 0,
                    MaxPreferences = 0,
                    MaxFacts = 0,
                    MaxTraces = 0,
                    MaxGraphRagItems = 0,
                    MinSimilarityScore = _options.MinSimilarityScore,
                    BlendMode = RetrievalBlendMode.MemoryOnly,
                    IncludeDiagnostics = evidenceQuestion is not null
                }
            },
            cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTelemetry(questionNumber, messages.Count, 0, false, "retrieval-exception");
            throw;
        }

        if (recall.TotalItemsRetrieved == 0)
        {
            RecordTelemetry(questionNumber, messages.Count, 0, recall.Truncated, "retrieval-empty");
            throw new InvalidOperationException(
                $"AgentMemory retrieved no history for LongMemEval question {questionNumber}; refusing to manufacture a score.");
        }

        var recalled = recall.Context.RelevantMessages.Items;
        if (recalled.Count == 0)
        {
            RecordTelemetry(questionNumber, messages.Count, recall.TotalItemsRetrieved, recall.Truncated, "retrieval-messages-empty");
            throw new InvalidOperationException(
                $"AgentMemory reported recalled items but no relevant messages for LongMemEval question {questionNumber}.");
        }

        var answerPrompt = BuildAnswerPrompt(
            recalled.Select(message => (message.Role, message.Content)),
            prompt);
        LongMemEvalRetrievalEvidence? retrievalEvidence = null;
        if (evidenceQuestion is not null)
        {
            try
            {
                retrievalEvidence = LongMemEvalRetrievalEvidence.Build(
                    evidenceQuestion,
                    recalled,
                    recall.Context.RelevantMessages.RankedItems,
                    originsByMessageId,
                    _options.EvidenceDetail,
                    answerPrompt.Length);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                RecordTelemetry(
                    questionNumber,
                    messages.Count,
                    recall.TotalItemsRetrieved,
                    recall.Truncated,
                    "retrieval-diagnostics-error",
                    evidenceQuestion.QuestionId);
                throw;
            }
        }

        ChatResponse response;
        try
        {
            response = await LongMemEvalRuntime.ExecuteStageAsync(
                "answer",
                () => _chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, answerPrompt)
            ],
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTelemetry(questionNumber, messages.Count, recall.TotalItemsRetrieved, recall.Truncated, "answer-error");
            throw;
        }

        RecordTelemetry(
            questionNumber,
            messages.Count,
            recall.TotalItemsRetrieved,
            recall.Truncated,
            "completed",
            evidenceQuestion?.QuestionId,
            retrievalEvidence);

        return new AgentResponse
        {
            Text = response.Text ?? string.Empty,
            ModelId = _options.ModelId,
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["agentMemory.sessionId"] = sessionId,
                ["agentMemory.ownerId"] = ownerId,
                ["agentMemory.messagesStored"] = messages.Count,
                ["agentMemory.itemsRetrieved"] = recall.TotalItemsRetrieved,
                ["agentMemory.truncated"] = recall.Truncated
            }
        };
    }

    private void RecordTelemetry(
        int questionNumber,
        int messagesStored,
        int itemsRetrieved,
        bool recallTruncated,
        string status,
        string? questionId = null,
        LongMemEvalRetrievalEvidence? retrievalEvidence = null)
    {
        lock (_stateLock)
        {
            _telemetry.Add(new LongMemEvalQuestionTelemetry(
                questionNumber, messagesStored, itemsRetrieved, recallTruncated, status)
            {
                QuestionId = questionId,
                RetrievalEvidence = retrievalEvidence
            });
        }
    }

    private List<Message> BuildMessages(
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        string sessionId,
        string ownerId,
        int questionNumber,
        LongMemEvalEvidenceQuestion? evidenceQuestion,
        IDictionary<string, LongMemEvalMessageOrigin> originsByMessageId)
    {
        var expectedCount = history.Count * 2;
        if (evidenceQuestion is not null && evidenceQuestion.Messages.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"LongMemEval evidence contained {evidenceQuestion.Messages.Count} origins for {expectedCount} injected messages.");
        }

        var result = new List<Message>(expectedCount);
        var ordinal = 0;
        foreach (var (user, assistant) in history)
        {
            result.Add(Message("user", user));
            result.Add(Message("assistant", assistant));
        }

        return result;

        Message Message(string role, string content)
        {
            var current = ordinal++;
            var messageId = $"{_runId}-q{questionNumber:D4}-m{current:D6}";
            var metadata = new Dictionary<string, object>
            {
                ["ownerId"] = ownerId,
                ["longMemEval"] = true,
                ["questionNumber"] = questionNumber
            };

            if (evidenceQuestion is not null)
            {
                var origin = evidenceQuestion.Messages[current];
                if (!string.Equals(origin.Role, role, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(origin.FormattedContent, content, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"LongMemEval source provenance did not align at message ordinal {current}.");
                }

                // These are source coordinates, not evaluation labels. In particular, HasAnswer and
                // AnswerSessionIds remain evaluator-side and are never persisted or sent to the answer model.
                metadata["sourceSessionId"] = origin.SourceSessionId;
                metadata["sourceSessionOrdinal"] = origin.SourceSessionOrdinal;
                metadata["sourceTimestamp"] = origin.SourceTimestamp;
                metadata["sourceSyntheticBoundary"] = origin.IsSyntheticBoundary;
                metadata["sourceSyntheticFormatterPadding"] = origin.IsSyntheticFormatterPadding;
                if (origin.SourceTurnOrdinal is int sourceTurnOrdinal)
                    metadata["sourceTurnOrdinal"] = sourceTurnOrdinal;
                originsByMessageId.Add(messageId, origin);
            }

            return new Message
            {
                MessageId = messageId,
                SessionId = sessionId,
                ConversationId = sessionId,
                Role = role,
                Content = content,
                TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(current),
                Metadata = metadata
            };
        }
    }

    internal static string BuildAnswerPrompt(
        IEnumerable<(string Role, string Content)> recalled,
        string question)
    {
        ArgumentNullException.ThrowIfNull(recalled);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var builder = new StringBuilder("Retrieved memory:\n");
        foreach (var (role, content) in recalled)
            builder.Append('[').Append(role).Append("] ").AppendLine(content);
        builder.Append("\nQuestion: ").Append(question).Append("\nAnswer:");
        return builder.ToString();
    }

    private string ScopeId(string kind, int question) => $"{_runId}-{kind}-{question:D4}";

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
}

public sealed record LongMemEvalAdapterOptions
{
    public int MaxRelevantMessages { get; init; } = 30;

    public double MinSimilarityScore { get; init; } = 0;

    public string? ModelId { get; init; }

    internal LongMemEvalEvidenceIndex? EvidenceIndex { get; init; }

    internal LongMemEvalEvidenceDetail EvidenceDetail { get; init; } =
        LongMemEvalEvidenceDetail.Identifiers;
}

public sealed record LongMemEvalQuestionTelemetry(
    int QuestionNumber,
    int MessagesStored,
    int ItemsRetrieved,
    bool RecallTruncated,
    string Status = "completed")
{
    public string? QuestionId { get; init; }

    public LongMemEvalRetrievalEvidence? RetrievalEvidence { get; init; }
}
