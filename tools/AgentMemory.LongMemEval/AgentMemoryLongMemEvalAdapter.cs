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
        if (_options.PreparedMemory &&
            (!_options.MemoryMode.UsesExtraction() ||
             !_options.RequireGraphReadBack ||
             _options.GraphProbe is null ||
             _options.EvidenceIndex is null ||
             _options.PreparedState is null))
        {
            throw new ArgumentException(
                "Prepared LongMemEval evaluation requires structured memory, sealed state, evidence, and graph read-back verification.",
                nameof(options));
        }
        if (_options.PreparedMemory &&
            (!string.Equals(
                 _options.PreparedState!.Manifest.AnswerModelId,
                 _options.ModelId,
                 StringComparison.Ordinal) ||
             _options.PreparedState.Manifest.MaxRelevantMessages != _options.MaxRelevantMessages))
        {
            throw new ArgumentException(
                "Prepared LongMemEval adapter configuration does not match the sealed manifest.",
                nameof(options));
        }
        if (_options.PreparationOnly &&
            (!_options.MemoryMode.UsesExtraction() ||
             !_options.RequireGraphReadBack ||
             _options.GraphProbe is null ||
             _options.EvidenceIndex is null ||
             _options.PreparedMemory))
        {
            throw new ArgumentException(
                "LongMemEval preparation requires unprepared structured memory, evidence, and graph read-back verification.",
                nameof(options));
        }

        if (_options.DiagnosticSourceSessionOrdinal is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The diagnostic source-session ordinal must be non-negative.");
        }
        if (_options.DiagnosticSourceSessionOrdinal is not null &&
            !_options.PreparationOnly)
        {
            throw new ArgumentException(
                "A diagnostic source-session selector is valid only for preparation-only execution.",
                nameof(options));
        }
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
        var timings = new LongMemEvalStageTimingCollector();

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
            _runId, history, sessionId, ownerId, questionNumber, evidenceQuestion, originsByMessageId);

        LongMemEvalPreparedQuestion? preparedQuestion = null;
        if (_options.PreparedMemory)
        {
            try
            {
                preparedQuestion = _options.PreparedState!.ValidateQuestion(
                    questionNumber, evidenceQuestion!, history, sessionId, ownerId);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                RecordTelemetry(
                    questionNumber, 0, 0, false, "prepared-manifest-mismatch",
                    evidenceQuestion?.QuestionId);
                throw;
            }
        }

        var messagesStored = 0;
        if (!_options.PreparedMemory)
        {
            try
            {
                _ = await timings.MeasureAsync(
                    LongMemEvalStage.Storage,
                    () => LongMemEvalRuntime.ExecuteStageAsync(
                        "storage",
                        () => _memory.AddMessagesAsync(messages, cancellationToken))).ConfigureAwait(false);
                messagesStored = messages.Count;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                RecordTelemetry(questionNumber, 0, 0, false, "storage-error");
                throw;
            }
        }

        var extractionUnits = 0;
        LongMemEvalGraphSnapshot? graphSnapshot = null;
        if (_options.MemoryMode.UsesExtraction())
        {
            if (evidenceQuestion is null)
            {
                RecordTelemetry(
                    questionNumber, messages.Count, 0, false, "extraction-provenance-missing");
                throw new InvalidOperationException(
                    "Structured LongMemEval modes require source-session provenance.");
            }

            if (!_options.PreparedMemory)
            {
                var allExtractionGroups = messages
                    .Select((message, index) =>
                        (Message: message, Origin: evidenceQuestion.Messages[index]))
                    .Where(item =>
                        !item.Origin.IsSyntheticBoundary &&
                        !item.Origin.IsSyntheticFormatterPadding)
                    .GroupBy(item => item.Origin.SourceSessionOrdinal)
                    .OrderBy(group => group.Key)
                    .ToArray();
                var extractionGroups =
                    _options.DiagnosticSourceSessionOrdinal is { } selected
                        ? allExtractionGroups
                            .Where(group => group.Key == selected)
                            .ToArray()
                        : allExtractionGroups;
                if (_options.DiagnosticSourceSessionOrdinal is not null &&
                    extractionGroups.Length != 1)
                    throw new InvalidOperationException(
                        "The diagnostic source-session ordinal does not exist in the selected question.");
                _options.ExtractionProgress?.Invoke(0, extractionGroups.Length);


                foreach (var group in extractionGroups)
                {
                    var sourceMessages = group.Select(item => item.Message).ToArray();
                    if (sourceMessages.Length == 0)
                        continue;

                    var callsBefore = _options.PreparationOnly &&
                        _chatClient is LongMemEvalChatCallMeter callMeter
                            ? callMeter.Snapshot() : null;
                    try
                    {
                        var extraction = await timings.MeasureAsync(
                            LongMemEvalStage.ExtractionPersistence,
                            () => LongMemEvalRuntime.ExecuteStageAsync(
                                "extraction",
                                () => _memory.ExtractAndPersistAsync(
                                    new ExtractionRequest
                                    {
                                        Messages = sourceMessages,
                                        SessionId = $"{sessionId}-source-{group.Key:D4}",
                                        UserId = ownerId
                                    },
                                    cancellationToken))).ConfigureAwait(false);
                        extractionUnits++;
                        _options.ExtractionProgress?.Invoke(extractionUnits, extractionGroups.Length);
                        if (callsBefore is not null &&
                            _chatClient is LongMemEvalChatCallMeter extractionCallMeter)
                        {
                            var callsAfter = extractionCallMeter.Snapshot();
                            var callDelta = callsAfter.Calls - callsBefore.Calls;
                            var failureDelta = callsAfter.Failures - callsBefore.Failures;
                            if (callDelta != 4 || failureDelta != 0)
                            {
                                var callDetails = callsAfter.CallDetails
                                    .Where(detail => detail.CallOrdinal > callsBefore.Calls)
                                    .ToArray();
                                var purposeSummary = string.Join(
                                    ", ",
                                    callDetails
                                        .GroupBy(detail => detail.Purpose)
                                        .OrderBy(group => group.Key, StringComparer.Ordinal)
                                        .Select(group => $"{group.Key}={group.Count()}"));
                                var callDetailSuffix = purposeSummary.Length == 0
                                    ? string.Empty
                                    : $" Call purposes: {purposeSummary}.";
                                var missingCallDetails =
                                    Math.Max(0, callDelta - callDetails.LongLength);
                                var failureDetails = callsAfter.FailureDetails
                                    .Where(detail => detail.CallOrdinal > callsBefore.Calls)
                                    .Select(detail =>
                                        $"call {detail.CallOrdinal}, purpose {detail.Purpose}, " +
                                        $"exception {detail.ExceptionType}, status " +
                                        $"{detail.ProviderStatus?.ToString() ?? "none"}")
                                    .ToArray();
                                var detailSuffix = failureDetails.Length == 0
                                    ? string.Empty
                                    : $" Failure details: {string.Join("; ", failureDetails)}.";
                                var droppedDelta =
                                    callsAfter.DroppedFailureDetails -
                                    callsBefore.DroppedFailureDetails;
                                RecordTelemetry(
                                    questionNumber,
                                    messages.Count,
                                    0,
                                    false,
                                    "extraction-provider-accounting-error",
                                    evidenceQuestion.QuestionId,
                                    extractionUnits: extractionUnits);
                                throw new LongMemEvalExtractionAccountingException(
                                    $"LongMemEval extraction provider accounting mismatch at " +
                                    $"question {questionNumber}, source session {group.Key}: " +
                                    $"observed {callDelta} calls and {failureDelta} failures; " +
                                    $"expected exactly 4 calls and zero failures.{callDetailSuffix}" +
                                    $"{detailSuffix} Missing unit call details: {missingCallDetails}. " +
                                    $"Dropped call details total: {callsAfter.DroppedCallDetails}. " +
                                    $"Dropped failure details: {droppedDelta}.");
                            }
                        }
                        if (extraction.Status != IngestionStatus.Succeeded)
                        {
                            RecordTelemetry(
                                questionNumber,
                                messages.Count,
                                0,
                                false,
                                "extraction-incomplete",
                                evidenceQuestion.QuestionId,
                                extractionUnits: extractionUnits);
                            throw new InvalidOperationException(
                                $"LongMemEval extraction unit {group.Key} did not complete successfully.");
                        }
                    }
                    catch (LongMemEvalExtractionAccountingException)
                    {
                        throw;
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        RecordTelemetry(
                            questionNumber,
                            messages.Count,
                            0,
                            false,
                            "extraction-error",
                            evidenceQuestion.QuestionId,
                            extractionUnits: extractionUnits);
                        throw;
                    }
                }
            }

            if (_options.RequireGraphReadBack)
            {
                if (_options.GraphProbe is null)
                {
                    throw new InvalidOperationException(
                        "Structured LongMemEval modes require a graph read-back probe.");
                }

                graphSnapshot = await timings.MeasureAsync(
                    LongMemEvalStage.GraphReadBack,
                    () => LongMemEvalRuntime.ExecuteStageAsync(
                        "graph read-back",
                        () => _options.GraphProbe.ReadAsync(ownerId, cancellationToken)))
                    .ConfigureAwait(false);
                if (graphSnapshot.TotalLearned == 0 || !graphSnapshot.CompleteProvenance)
                {
                    RecordTelemetry(
                        questionNumber,
                        messages.Count,
                        0,
                        false,
                        graphSnapshot.TotalLearned == 0
                            ? "graph-readback-empty"
                            : "graph-provenance-incomplete",
                        evidenceQuestion.QuestionId,
                        extractionUnits: extractionUnits,
                        graphSnapshot: graphSnapshot);
                    throw new InvalidOperationException(
                        "LongMemEval graph read-back did not prove non-empty learned memory with complete provenance.");
                }

                if (preparedQuestion is not null &&
                    !Equals(graphSnapshot, preparedQuestion.GraphSnapshot))
                {
                    RecordTelemetry(
                        questionNumber,
                        0,
                        0,
                        false,
                        "prepared-graph-mismatch",
                        evidenceQuestion.QuestionId,
                        graphSnapshot: graphSnapshot,
                        messagesPrepared: preparedQuestion.MessagesPrepared,
                        extractionUnitsPrepared: preparedQuestion.ExtractionUnitsPrepared,
                        preparedMemory: true);
                    throw new InvalidOperationException(
                        $"Prepared LongMemEval graph state does not match the sealed snapshot for question {questionNumber}.");
                }
            }
        }

        if (_options.PreparationOnly)
        {
            RecordTelemetry(
                questionNumber, messagesStored, 0, false, "prepared",
                evidenceQuestion!.QuestionId, extractionUnits: extractionUnits,
                graphSnapshot: graphSnapshot, stageTimings: timings.Snapshot());
            return new AgentResponse { Text = string.Empty, ModelId = _options.ModelId };
        }

        RecallResult recall;
        try
        {
            var budget = LongMemEvalRecallBudget.For(
                _options.MemoryMode, _options.MaxRelevantMessages);
            recall = await timings.MeasureAsync(
                LongMemEvalStage.Retrieval,
                () => LongMemEvalRuntime.ExecuteStageAsync(
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
                                MaxRelevantMessages = budget.Messages,
                                MaxEntities = budget.Entities,
                                MaxPreferences = budget.Preferences,
                                MaxFacts = budget.Facts,
                                MaxTraces = 0,
                                MaxGraphRagItems = budget.GraphRag,
                                MinSimilarityScore = _options.MinSimilarityScore,
                                BlendMode = RetrievalBlendMode.MemoryOnly,
                                IncludeDiagnostics = evidenceQuestion is not null
                            }
                        },
                        cancellationToken))).ConfigureAwait(false);
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
        var structuredItems =
            recall.Context.RelevantEntities.Items.Count +
            recall.Context.RelevantFacts.Items.Count +
            recall.Context.RelevantPreferences.Items.Count;
        if (_options.MemoryMode == LongMemEvalMemoryMode.Raw && recalled.Count == 0)
        {
            RecordTelemetry(questionNumber, messages.Count, recall.TotalItemsRetrieved, recall.Truncated, "retrieval-messages-empty");
            throw new InvalidOperationException(
                $"AgentMemory reported recalled items but no relevant messages for LongMemEval question {questionNumber}.");
        }

        if (_options.MemoryMode == LongMemEvalMemoryMode.Structured && structuredItems == 0)
        {
            RecordTelemetry(
                questionNumber,
                messages.Count,
                recall.TotalItemsRetrieved,
                recall.Truncated,
                "retrieval-structured-empty",
                evidenceQuestion?.QuestionId,
                extractionUnits: extractionUnits);
            throw new InvalidOperationException(
                $"AgentMemory retrieved no structured memory for LongMemEval question {questionNumber}.");
        }

        var answerPrompt = BuildAnswerPrompt(recall.Context, prompt);
        LongMemEvalRetrievalEvidence? retrievalEvidence = null;
        AgentEval.Memory.External.Models.QuestionEvidenceEnvelope? normalizedEvidence = null;
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
                if (_options.EvidenceDetail != LongMemEvalEvidenceDetail.None)
                {
                    normalizedEvidence = LongMemEvalAgentEvalEvidence.Build(
                        recall.Context, originsByMessageId, _options.EvidenceDetail);
                }
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
            response = await timings.MeasureAsync(
                LongMemEvalStage.Answer,
                () => LongMemEvalRuntime.ExecuteStageAsync(
                    "answer",
                    () => _chatClient.GetResponseAsync(
                        [
                            new ChatMessage(ChatRole.System, SystemPrompt),
                            new ChatMessage(ChatRole.User, answerPrompt)
                        ],
                        cancellationToken: cancellationToken))).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTelemetry(questionNumber, messages.Count, recall.TotalItemsRetrieved, recall.Truncated, "answer-error");
            throw;
        }

        RecordTelemetry(
            questionNumber,
            messagesStored,
            recall.TotalItemsRetrieved,
            recall.Truncated,
            "completed",
            evidenceQuestion?.QuestionId,
            retrievalEvidence,
            extractionUnits,
            recall.Context,
            graphSnapshot,
            timings.Snapshot(),
            preparedQuestion?.MessagesPrepared ?? 0,
            preparedQuestion?.ExtractionUnitsPrepared ?? 0,
            preparedQuestion is not null);

        var additionalProperties = new Dictionary<string, object?>
        {
            ["agentMemory.sessionId"] = sessionId,
            ["agentMemory.ownerId"] = ownerId,
            ["agentMemory.messagesStored"] = messagesStored,
            ["agentMemory.itemsRetrieved"] = recall.TotalItemsRetrieved,
            ["agentMemory.truncated"] = recall.Truncated
        };
        if (normalizedEvidence is not null)
            additionalProperties[AgentEval.Memory.External.Models.QuestionEvidenceEnvelope.AdditionalPropertiesKey] =
                normalizedEvidence;

        return new AgentResponse
        {
            Text = response.Text ?? string.Empty,
            ModelId = _options.ModelId,
            AdditionalProperties = additionalProperties
        };
    }

    private void RecordTelemetry(
        int questionNumber,
        int messagesStored,
        int itemsRetrieved,
        bool recallTruncated,
        string status,
        string? questionId = null,
        LongMemEvalRetrievalEvidence? retrievalEvidence = null,
        int extractionUnits = 0,
        MemoryContext? context = null,
        LongMemEvalGraphSnapshot? graphSnapshot = null,
        LongMemEvalStageTimings? stageTimings = null,
        int messagesPrepared = 0,
        int extractionUnitsPrepared = 0,
        bool preparedMemory = false)
    {
        lock (_stateLock)
        {
            _telemetry.Add(new LongMemEvalQuestionTelemetry(
                questionNumber, messagesStored, itemsRetrieved, recallTruncated, status)
            {
                QuestionId = questionId,
                RetrievalEvidence = retrievalEvidence,
                ExtractionUnits = extractionUnits,
                MessagesPrepared = messagesPrepared,
                ExtractionUnitsPrepared = extractionUnitsPrepared,
                PreparedMemory = preparedMemory,
                RawMessagesRetrieved = context?.RelevantMessages.Items.Count ?? 0,
                EntitiesRetrieved = context?.RelevantEntities.Items.Count ?? 0,
                FactsRetrieved = context?.RelevantFacts.Items.Count ?? 0,
                PreferencesRetrieved = context?.RelevantPreferences.Items.Count ?? 0,
                GraphRagIncluded = !string.IsNullOrWhiteSpace(context?.GraphRagContext),
                GraphReadBack = graphSnapshot,
                StageTimings = stageTimings
            });
        }
    }

    internal static List<Message> BuildMessages(
        string runId,
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
            var messageId = $"{runId}-q{questionNumber:D4}-m{current:D6}";
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

    internal static string BuildAnswerPrompt(MemoryContext context, string question)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var builder = new StringBuilder("Retrieved memory:\n");
        foreach (var message in context.RelevantMessages.Items)
            builder.Append('[').Append(message.Role).Append("] ").AppendLine(message.Content);
        foreach (var entity in context.RelevantEntities.Items)
        {
            builder.Append("[entity] ").Append(entity.Name).Append(" (").Append(entity.Type).Append(')');
            if (!string.IsNullOrWhiteSpace(entity.Description))
                builder.Append(": ").Append(entity.Description);
            builder.AppendLine();
        }
        foreach (var fact in context.RelevantFacts.Items)
        {
            builder.Append("[fact] ")
                .Append(fact.Subject).Append(' ')
                .Append(fact.Predicate).Append(' ')
                .Append(fact.Object);
            if (fact.ValidFrom is not null || fact.ValidUntil is not null)
            {
                builder.Append(" [valid ")
                    .Append(fact.ValidFrom?.ToString("O") ?? "?")
                    .Append(" to ")
                    .Append(fact.ValidUntil?.ToString("O") ?? "?")
                    .Append(']');
            }
            builder.AppendLine();
        }
        foreach (var preference in context.RelevantPreferences.Items)
        {
            builder.Append("[preference] ").Append(preference.PreferenceText);
            if (!string.IsNullOrWhiteSpace(preference.Context))
                builder.Append(" (").Append(preference.Context).Append(')');
            builder.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(context.GraphRagContext))
            builder.Append("[graphrag]\n").AppendLine(context.GraphRagContext);
        builder.Append("\nQuestion: ").Append(question).Append("\nAnswer:");
        return builder.ToString();
    }

    private string ScopeId(string kind, int question) => $"{_runId}-{kind}-{question:D4}";

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
}

internal sealed class LongMemEvalExtractionAccountingException
    : InvalidOperationException
{
    public LongMemEvalExtractionAccountingException(string message)
        : base(message)
    {
    }
}

public sealed record LongMemEvalAdapterOptions
{
    public LongMemEvalMemoryMode MemoryMode { get; init; } = LongMemEvalMemoryMode.Raw;

    public bool PreparedMemory { get; init; }

    public LongMemEvalPreparedState? PreparedState { get; init; }

    internal bool PreparationOnly { get; init; }


    internal int? DiagnosticSourceSessionOrdinal { get; init; }
    /// <summary>
    /// Total non-GraphRAG answer-context item budget. Raw uses it entirely for messages; Structured
    /// divides it across entities/facts/preferences; Hybrid gives half to messages and divides the
    /// remainder across structured categories.
    /// </summary>

    public int MaxRelevantMessages { get; init; } = 30;

    public double MinSimilarityScore { get; init; } = 0;

    public string? ModelId { get; init; }

    internal LongMemEvalEvidenceIndex? EvidenceIndex { get; init; }

    internal LongMemEvalEvidenceDetail EvidenceDetail { get; init; } =
        LongMemEvalEvidenceDetail.Identifiers;


    internal Action<int, int>? ExtractionProgress { get; init; }

    internal bool RequireGraphReadBack { get; init; }

    internal ILongMemEvalGraphProbe? GraphProbe { get; init; }
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

    public int ExtractionUnits { get; init; }

    public int MessagesPrepared { get; init; }

    public int ExtractionUnitsPrepared { get; init; }

    public bool PreparedMemory { get; init; }

    public int RawMessagesRetrieved { get; init; }

    public int EntitiesRetrieved { get; init; }

    public int FactsRetrieved { get; init; }

    public int PreferencesRetrieved { get; init; }

    public bool GraphRagIncluded { get; init; }

    public LongMemEvalGraphSnapshot? GraphReadBack { get; init; }

    public LongMemEvalStageTimings? StageTimings { get; init; }
}

internal sealed record LongMemEvalRecallBudget(
    int Messages,
    int Entities,
    int Facts,
    int Preferences,
    int GraphRag)
{
    internal static LongMemEvalRecallBudget For(LongMemEvalMemoryMode mode, int total)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(total);
        return mode switch
        {
            LongMemEvalMemoryMode.Raw => new(total, 0, 0, 0, 0),
            LongMemEvalMemoryMode.Structured => Structured(total),
            LongMemEvalMemoryMode.Hybrid => Hybrid(total),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private static LongMemEvalRecallBudget Structured(int total)
    {
        var each = total / 3;
        return new(0, each, total - each * 2, each, 0);
    }

    private static LongMemEvalRecallBudget Hybrid(int total)
    {
        var messages = total / 2;
        var remaining = total - messages;
        var each = remaining / 3;
        return new(messages, each, remaining - each * 2, each, 0);
    }
}
