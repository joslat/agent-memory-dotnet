using AgentMemory.Core.Memory;
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
public sealed partial class AgentMemoryLongMemEvalAdapter :
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
        if (_options.InitialQuestionNumber < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The initial question number must be non-negative.");
        }
        if (_options.UseBatchedPreparation &&
            (!_options.PreparationOnly ||
             _options.DiagnosticSourceSessionOrdinal is not null ||
             _options.BatchExtractionPipeline is null ||
             _options.BatchPlanner is null ||
             _options.ExpectedExtractionPlan is null ||
             _options.MaxSessionsPerBatch <= 0 ||
             _options.MaxInputTokens <= 0))
        {
            throw new ArgumentException(
                "Batched LongMemEval preparation requires an ordinary preparation run, the batch pipeline, deterministic planner, expected plan, and positive batch limits.",
                nameof(options));
        }
        if (_options.ExpandFactsByPredicate)
        {
            // Checked here rather than discovered mid-run: this exact overflow cost two full
            // 121-call rebuilds to surface as an opaque diagnostics error. Worst case is every
            // category filled plus every expanded fact, and AgentEval rejects the envelope above
            // MaximumReferences.
            var budget = LongMemEvalRecallBudget.For(
                _options.MemoryMode, _options.MaxRelevantMessages) with
            {
                GraphRag = _options.GraphRagItems
            };
            var worstCaseReferences =
                budget.Messages + budget.Entities + budget.Facts + budget.Preferences +
                _options.MaxExpandedFacts;
            if (worstCaseReferences > AgentEval.Memory.External.Models.QuestionEvidenceEnvelope.MaximumReferences)
            {
                throw new ArgumentException(
                    $"Predicate expansion would produce up to {worstCaseReferences} evidence " +
                    $"references, exceeding AgentEval's maximum of " +
                    $"{AgentEval.Memory.External.Models.QuestionEvidenceEnvelope.MaximumReferences}. " +
                    $"Lower MaxExpandedFacts (currently {_options.MaxExpandedFacts}) or the recall " +
                    "budget so the total fits.",
                    nameof(options));
            }
        }

        _questionNumber = _options.InitialQuestionNumber;
        _sessionId = ScopeId("session", _questionNumber);
        _ownerId = ScopeId("owner", _questionNumber);
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
                // G3B.9 root fix. AgentEval's formatter has no channel for session structure in an
                // API that only accepts (user, assistant) pairs, so it fabricates a turn per session
                // boundary — `LongMemEvalHistoryFormatter.cs:39`. The assistant half
                // ("Understood. Starting a new conversation session.") is pure fabrication carrying
                // zero information, and the user half's session id and date are already attached to
                // every real message as provenance metadata. Persisting them was therefore storing
                // 21% redundant corpus whose *identical* content produced identical embeddings, tying
                // and monopolising top-K — 46 byte-identical copies in one question.
                //
                // Excluding them at the write boundary removes the flood at its source rather than
                // filtering it back out at retrieval. `messages` itself is left whole because the
                // extraction path indexes it positionally against the evidence origins.
                var persisted = SelectPersistableMessages(messages, originsByMessageId);
                _ = await timings.MeasureAsync(
                    LongMemEvalStage.Storage,
                    () => LongMemEvalRuntime.ExecuteStageAsync(
                        "storage",
                        () => _memory.AddMessagesAsync(persisted, cancellationToken))).ConfigureAwait(false);
                messagesStored = persisted.Count;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                RecordTelemetry(questionNumber, 0, 0, false, "storage-error");
                throw;
            }
        }

        var extractionUnits = 0;
        var extractionCallsPlanned = 0;
        LongMemEvalGraphSnapshot? graphSnapshot = null;
        LongMemEvalGoldEvidenceCoverage? goldCoverage = null;
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
                if (_options.UseBatchedPreparation)
                {
                    try
                    {
                        var batch = await ExecuteBatchedPreparationAsync(
                            messages,
                            evidenceQuestion,
                            sessionId,
                            ownerId,
                            questionNumber,
                            timings,
                            cancellationToken).ConfigureAwait(false);
                        extractionUnits = batch.ExtractionUnits;
                        extractionCallsPlanned = batch.PlannedCalls;
                    }
                    catch (LongMemEvalExtractionAccountingException)
                    {
                        RecordTelemetry(
                            questionNumber,
                            messages.Count,
                            0,
                            false,
                            "extraction-provider-accounting-error",
                            evidenceQuestion.QuestionId,
                            extractionUnits: extractionUnits,
                            extractionCallsPlanned:
                                _options.ExpectedExtractionPlan!.BatchCount);
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
                else
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

                // G3B.5. Soundness is proven above; this asks whether the build is *adequate* —
                // whether anything was learned from the sessions that actually hold the answer.
                // Checked here, before any evaluation call is spent on this graph.
                var goldSourceMessageIds = originsByMessageId
                    .Where(entry =>
                        evidenceQuestion.AnswerSessionIds.Contains(entry.Value.SourceSessionId))
                    .Select(entry => entry.Key)
                    .ToArray();
                goldCoverage = await _options.GraphProbe
                    .ReadGoldCoverageAsync(ownerId, goldSourceMessageIds, cancellationToken)
                    .ConfigureAwait(false);

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
                graphSnapshot: graphSnapshot, stageTimings: timings.Snapshot(),
                extractionCallsPlanned: extractionCallsPlanned,
                goldCoverage: goldCoverage);
            return new AgentResponse { Text = string.Empty, ModelId = _options.ModelId };
        }

        // Hoisted out of the try: the retrieval-evidence build below needs the message allowance to
        // tell "retrieval missed" apart from "retrieval was never given a message budget" (BUG-E1).
        var budget = LongMemEvalRecallBudget.For(
            _options.MemoryMode, _options.MaxRelevantMessages) with
        {
            GraphRag = _options.GraphRagItems
        };
        // G3B.1 over-fetches candidates so that dropping formatter artifacts still fills the budget.
        // The final cap stays `budget.Messages`; only the request widens.
        var requestedMessages = _options.ExcludeSyntheticFormatterMessages
            ? budget.Messages * _options.SyntheticExclusionCandidateMultiplier
            : budget.Messages;
        RecallResult recall;
        try
        {
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
                                MaxRelevantMessages = requestedMessages,
                                MaxEntities = budget.Entities,
                                MaxPreferences = budget.Preferences,
                                MaxFacts = budget.Facts,
                                MaxTraces = 0,
                                // G5 "hard" tier: a relation returned whole, for the aggregation
                                // questions top-K structurally cannot answer.
                                ExpandFactsByPredicate = _options.ExpandFactsByPredicate,
                                // J2.2: also expand on the relations the question itself names, for
                                // the multi-relation case top-K structurally cannot nominate.
                                ResolveQueryRelations = _options.ResolveQueryRelations,
                                MaxExpandedFacts = _options.MaxExpandedFacts,
                                MaxGraphRagItems = budget.GraphRag,
                                MinSimilarityScore = _options.MinSimilarityScore,
                                BlendMode = BlendModeFor(budget.GraphRag),
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

        if (_options.ExcludeSyntheticFormatterMessages)
        {
            recall = recall with
            {
                Context = recall.Context with
                {
                    RelevantMessages = LongMemEvalRecallBudget.SelectRealSourceTurns(
                        recall.Context.RelevantMessages,
                        originsByMessageId,
                        budget.Messages,
                        _options.MaxItemsPerSourceSession)
                }
            };
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

        if (_options.ChronologicalAnswerContext)
        {
            // The stored clock is epoch + injection ordinal, so ordering by it reproduces the
            // conversation's own sequence exactly. Selection is unchanged; only the order differs.
            recall = recall with
            {
                Context = recall.Context with
                {
                    RelevantMessages = recall.Context.RelevantMessages with
                    {
                        Items = recall.Context.RelevantMessages.Items
                            .OrderBy(message => message.TimestampUtc)
                            .ToArray()
                    }
                }
            };
        }

        var answerPrompt = BuildAnswerPrompt(
            recall.Context, prompt, evidenceQuestion?.QuestionDate, originsByMessageId);
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
                    answerPrompt.Length,
                    budget.Messages);
                if (_options.EvidenceDetail != LongMemEvalEvidenceDetail.None)
                {
                    normalizedEvidence = LongMemEvalAgentEvalEvidence.Build(
                        recall.Context, originsByMessageId, _options.EvidenceDetail);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Content-free but specific: the type and message locate the failing builder, which
                // a bare status cannot. Diagnosing this by inspection previously cost two full
                // 121-call rebuilds.
                RecordTelemetry(
                    questionNumber,
                    messages.Count,
                    recall.TotalItemsRetrieved,
                    recall.Truncated,
                    $"retrieval-diagnostics-error:{exception.GetType().Name}:{exception.Message}",
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

        // L2. Ask the graph how many live facts exist under the relation(s) this question named.
        // Null when no probe is wired or nothing resolved - "not measured", never "complete".
        var relationStoredKeys = (recall.Context.ResolvedQueryRelations ?? [])
            .SelectMany(MemoryRelationLexicon.Default.StoredFormsOf)
            .Where(key => !string.IsNullOrEmpty(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyDictionary<string, int>? relationGraphCounts = null;
        int? relationUnionGraphTotal = null;
        if (_options.GraphProbe is not null && relationStoredKeys.Length > 0)
        {
            relationGraphCounts = await _options.GraphProbe
                .ReadRelationFactCountsAsync(ownerId, relationStoredKeys, cancellationToken)
                .ConfigureAwait(false);

            // L13a. The budget is shared with the top-K hits' own predicates, so the union - not the
            // question's relations alone - is what decides whether it was exhausted.
            var unionKeys = recall.Context.RelevantFacts.Items
                .Select(fact => MemoryTripleCanonicalizer.Canonical(fact.Predicate))
                .Concat(relationStoredKeys)
                .Where(key => !string.IsNullOrEmpty(key))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var unionCounts = await _options.GraphProbe
                .ReadRelationFactCountsAsync(ownerId, unionKeys, cancellationToken)
                .ConfigureAwait(false);
            relationUnionGraphTotal = unionCounts?.Values.Sum();
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
            preparedQuestion is not null,
            goldCoverage: goldCoverage,
            // Computed here rather than inside RecordTelemetry, which has neither the gold message
            // origins nor the evidence question in scope.
            // L2. The stored predicate keys expansion would have searched, widened exactly as
            // LongTermMemoryService does - canonical relation -> every stored form - or the metric
            // would measure a different query than the one that ran.
            // Absent, not an all-null object, when nothing was measured - the same convention
            // ReadGoldCoverageAsync uses, and what keeps "no probe wired" distinguishable from
            // "measured and found nothing".
            relationCompleteness: relationGraphCounts is null
                ? null
                : ComputeRelationCompleteness(
                    relationStoredKeys,
                    relationGraphCounts,
                    recall.Context.RelevantFacts.Items,
                    _options.MaxExpandedFacts,
                    // The union expansion actually searched: the question's relations PLUS the
                    // canonical predicate of every top-K hit, which is what shares the one budget.
                    unionGraphTotal: relationUnionGraphTotal),
            retrievedGoldCoverage: RetrievedGoldCoverage(
                recall.Context.RelevantFacts.Items,
                originsByMessageId
                    .Where(entry => evidenceQuestion is not null &&
                                    evidenceQuestion.AnswerSessionIds.Contains(
                                        entry.Value.SourceSessionId))
                    .Select(entry => entry.Key)
                    .ToArray()),
            answerPromptText: answerPrompt);

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
        bool preparedMemory = false,
        int extractionCallsPlanned = 0,
        LongMemEvalGoldEvidenceCoverage? goldCoverage = null,
        double? retrievedGoldCoverage = null,
        LongMemEvalRelationCompleteness? relationCompleteness = null,
        string? answerPromptText = null)
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
                ExtractionCallsPlanned = extractionCallsPlanned,
                FactsRetrieved = context?.RelevantFacts.Items.Count ?? 0,
                PreferencesRetrieved = context?.RelevantPreferences.Items.Count ?? 0,
                GraphRagIncluded = !string.IsNullOrWhiteSpace(context?.GraphRagContext),
                // K6. "Included" only ever said the string was non-empty. These two say how many
                // passages came back and how many of them the structured surface had already
                // retrieved - the difference between a surface that adds evidence and one that
                // re-fetches it.
                // Runs on the PREPARED path, where the existing gold probe never does - it sits
                // inside `if (!PreparedMemory)`, so every prepared-pair report has a null coverage
                // and the n=50 result could not say why Structured loses multi-session questions.
                RetrievedGoldCoverage = retrievedGoldCoverage,
                RelationCompleteness = relationCompleteness,
                // Makes "expansion had nothing to expand" visible per question, instead of
                // requiring the lexicon to be consulted by hand after a run.
                ResolvedQueryRelations = context?.ResolvedQueryRelations ?? [],
                GraphRagItemsRetrieved = context?.GraphRagItems.Count ?? 0,
                GraphRagFactsAlreadyRetrieved = context is null
                    ? 0
                    : CountGraphRagFactsAlreadyRetrieved(
                        context.GraphRagItems, context.RelevantFacts.Items),
                // J5.1. The real cost of this arm: the assembled prompt the reader actually sees.
                // Every quality number here was half a result without it, and the band's cost column
                // predates predicate expansion entirely.
                AnswerPromptCharacters = answerPromptText?.Length ?? 0,
                EstimatedContextTokens = LongMemEvalContextSize.Estimate(answerPromptText),
                GraphReadBack = graphSnapshot,
                GoldEvidenceCoverage = goldCoverage,
                StageTimings = stageTimings
            });
        }
    }



    /// <summary>
    /// K6. The blend mode a GraphRAG budget requires, and the reason a budget alone is not enough.
    /// </summary>
    /// <remarks>
    /// <c>MemoryOnly</c> means "GraphRAG suppressed even when enabled", and the assembler checks the
    /// blend mode <i>before</i> the budget - so a non-zero <c>MaxGraphRagItems</c> under
    /// <c>MemoryOnly</c> retrieves nothing at all. The first K6 run measured exactly that and came
    /// within one step of reporting a confident zero as a property of the surface.
    /// <para>
    /// Every arm keeps <c>MemoryOnly</c> unless a GraphRAG budget was actually asked for, so every
    /// run this track has already produced stays comparable.
    /// </para>
    /// </remarks>
    internal static RetrievalBlendMode BlendModeFor(int graphRagBudget) =>
        graphRagBudget > 0 ? RetrievalBlendMode.Blended : RetrievalBlendMode.MemoryOnly;



    /// <summary>
    /// L2. Whether the context received every live fact under the relation(s) the question names.
    /// </summary>
    /// <remarks>
    /// The instrument Phase L exists to build, and the two numbers do different jobs.
    /// <para>
    /// <b>Denominator</b> counts the graph and is a <b>deterministic extraction-quality signal</b>:
    /// a Cypher <c>count()</c>, immune to answer-model and judge non-determinism. If a change stops
    /// learning a relation the questions need, it drops and says so. Nothing else in this repository
    /// can see that — the deterministic fixture sits at 1.000 by construction, and the LongMemEval
    /// channel carries sd 9.3 cold-build.
    /// </para>
    /// <para>
    /// <b>Numerator</b> counts the context and is a retrieval signal. Keeping them apart is the
    /// point: <c>Denominator = 0</c> with a non-empty key set means the relation was never
    /// extracted, while <c>Numerator &lt; Denominator</c> means it was extracted and retrieval left
    /// some behind. A single ratio renders an extraction bug and a retrieval bug identical, which is
    /// how the saturated message-coverage metric misled this track once already.
    /// </para>
    /// </remarks>
    internal static LongMemEvalRelationCompleteness ComputeRelationCompleteness(
        IReadOnlyList<string> storedPredicateKeys,
        IReadOnlyDictionary<string, int>? graphCounts,
        IReadOnlyCollection<Fact> retrievedFacts,
        int expansionLimit = 0,
        int? unionGraphTotal = null)
    {
        ArgumentNullException.ThrowIfNull(storedPredicateKeys);
        ArgumentNullException.ThrowIfNull(retrievedFacts);

        // No relation resolved, or the probe could not answer: null throughout. "Not measured" must
        // never be reported as complete, and it must never be reported as zero either.
        if (storedPredicateKeys.Count == 0 || graphCounts is null)
            return new LongMemEvalRelationCompleteness { StoredPredicateKeys = storedPredicateKeys };

        var keys = storedPredicateKeys.ToHashSet(StringComparer.Ordinal);
        var denominator = graphCounts
            .Where(pair => keys.Contains(pair.Key))
            .Sum(pair => pair.Value);
        var numerator = retrievedFacts
            .Where(fact => keys.Contains(MemoryTripleCanonicalizer.Canonical(fact.Predicate)))
            .Select(fact => fact.FactId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new LongMemEvalRelationCompleteness
        {
            StoredPredicateKeys = storedPredicateKeys,
            PerKeyGraphCounts = graphCounts.Where(p => keys.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Denominator = denominator,
            Numerator = numerator,
            // A relation the graph does not hold is an extraction miss, not a retrieval one, so the
            // ratio stays null rather than becoming a misleading 0.0.
            Ratio = denominator == 0 ? null : (double)numerator / denominator,
            Complete = denominator == 0 ? null : numerator >= denominator,
            RelationAbsentFromGraph = denominator == 0,
            // L13a. The budget is ONE shared LIMIT over the whole predicate union - the question's
            // relations plus the canonical predicate of every top-K vector hit - ordered globally by
            // confidence. So it binds when the UNION exceeds it, not when this relation alone does.
            //
            // The first definition compared `denominator > expansionLimit`, i.e. this relation
            // against the budget, and reported false on every real question while the budget was
            // exhausted on all of them: one question held 49 facts under its relation, had a 60-row
            // budget, and received 22 because 38 slots went to unrelated higher-confidence
            // predicates. Null when the union total was not measured, because "unknown" must never
            // read as "the budget was fine" - that is precisely the error being corrected.
            LimitBinding = unionGraphTotal is null || expansionLimit <= 0
                ? null
                : unionGraphTotal > expansionLimit,
            UnionGraphTotal = unionGraphTotal,
            ExpansionLimit = expansionLimit,
        };
    }

    /// <summary>
    /// How much of a question's gold evidence the <b>retrieved facts</b> actually carry.
    /// </summary>
    /// <remarks>
    /// The existing gold-coverage probe asks whether anything was <i>learned</i> from the gold
    /// sessions, and it runs only during preparation — so every prepared-pair report to date has a
    /// null coverage figure, and the n=50 result could say Structured loses multi-session questions
    /// without being able to say why.
    /// <para>
    /// This is the retrieval-side half. A fact covers a gold message when its
    /// <c>SourceMessageIds</c> contains it, so intersecting the retrieved facts' provenance with the
    /// gold message set separates three very different failures that look identical in a score:
    /// the evidence was never extracted, it was extracted but not retrieved, or it was retrieved and
    /// the reader still got the answer wrong. Only the second is a retrieval problem.
    /// </para>
    /// <para>
    /// Returns null when the question has no gold messages, because zero coverage of nothing is not
    /// a miss and must not be averaged in as one.
    /// </para>
    /// </remarks>
    internal static double? RetrievedGoldCoverage(
        IReadOnlyCollection<Fact> retrievedFacts,
        IReadOnlyCollection<string> goldSourceMessageIds)
    {
        ArgumentNullException.ThrowIfNull(retrievedFacts);
        ArgumentNullException.ThrowIfNull(goldSourceMessageIds);
        if (goldSourceMessageIds.Count == 0)
            return null;

        var covered = retrievedFacts
            .SelectMany(fact => fact.SourceMessageIds)
            .ToHashSet(StringComparer.Ordinal);
        return (double)goldSourceMessageIds.Count(covered.Contains) / goldSourceMessageIds.Count;
    }

    /// <summary>
    /// K6. How many GraphRAG items name a fact the structured surface already retrieved.
    /// </summary>
    /// <remarks>
    /// The locked prediction for K6 is that a GraphRAG budget pointed at the memory layer's own fact
    /// index returns the same rows the Structured arm already has - the same data fetched twice under
    /// a second budget. This counts that directly rather than inferring it from a score.
    /// <para>
    /// Identity comes from the <c>fact_id</c> the harness projects in its retrieval query. An item
    /// without one is counted as <i>not</i> duplicated: with the default projection there is no node
    /// identity at all (K10), and guessing by text would quietly turn "cannot tell" into "distinct".
    /// </para>
    /// </remarks>
    internal static int CountGraphRagFactsAlreadyRetrieved(
        IReadOnlyList<GraphRagContextItem> graphRagItems,
        IEnumerable<Fact> retrievedFacts)
    {
        ArgumentNullException.ThrowIfNull(graphRagItems);
        ArgumentNullException.ThrowIfNull(retrievedFacts);

        var retrievedIds = retrievedFacts
            .Select(fact => fact.FactId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);

        return graphRagItems.Count(item =>
            item.Metadata is not null &&
            item.Metadata.TryGetValue("fact_id", out var id) &&
            id?.ToString() is { Length: > 0 } factId &&
            retrievedIds.Contains(factId));
    }

    /// <summary>
    /// G3B.9. The messages that represent actual conversation, excluding AgentEval's fabricated
    /// session-boundary turns. An unclassifiable message is kept: dropping what we cannot identify
    /// would silently lose real evidence, which is far worse than the flooding this prevents.
    /// </summary>
    internal static List<Message> SelectPersistableMessages(
        IReadOnlyList<Message> messages,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> originsByMessageId)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(originsByMessageId);
        if (originsByMessageId.Count == 0)
            return messages.ToList();

        return messages
            .Where(message =>
                !originsByMessageId.TryGetValue(message.MessageId, out var origin) ||
                (!origin.IsSyntheticBoundary && !origin.IsSyntheticFormatterPadding))
            .ToList();
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

    /// <summary>
    /// G3B.2. The source timestamp AgentMemory persisted with the message and returns through recall.
    /// </summary>
    /// <remarks>
    /// LongMemEval session dates reach us only inside AgentEval's <c>--- Session N (date) ---</c>
    /// boundary markers, which G3B.1 correctly drops as formatter boilerplate — taking every date
    /// with them. The date survives on each real message as <c>sourceTimestamp</c> provenance, so it
    /// is restored from there rather than from the evaluator-side index: this must be data the
    /// product actually returns, not knowledge the harness happens to hold. Falls back to the stored
    /// clock so a message with no provenance is still rendered rather than silently dropped.
    /// </remarks>
    internal static string DisplayTimestamp(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Metadata is not null &&
               message.Metadata.TryGetValue("sourceTimestamp", out var source) &&
               source?.ToString() is { Length: > 0 } text
            ? text
            : message.TimestampUtc.ToString("O");
    }

    internal static string BuildAnswerPrompt(
        IEnumerable<(string Role, string Timestamp, string Content)> recalled,
        string question,
        string? currentDate = null)
    {
        ArgumentNullException.ThrowIfNull(recalled);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var builder = new StringBuilder("Retrieved memory:\n");
        foreach (var (role, timestamp, content) in recalled)
            AppendMessage(builder, role, timestamp, content);
        return AppendQuestion(builder, question, currentDate);
    }

    internal static string BuildAnswerPrompt(
        MemoryContext context,
        string question,
        string? currentDate = null,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin>? originsByMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        // G3B.7. Structured items carried no date at all, which is the same defect G3B.2 fixed for
        // messages, left live on the structured channel: Structured mode lost precisely the temporal
        // questions. Entities, facts and preferences all carry SourceMessageIds, and those messages
        // carry the real conversation date, so the item can be dated from data recall already
        // returns - no extra query and nothing evaluator-side.
        string SourceDates(IReadOnlyList<string> sourceMessageIds)
        {
            if (originsByMessageId is null || sourceMessageIds.Count == 0)
                return string.Empty;
            var dates = sourceMessageIds
                .Select(id => originsByMessageId.TryGetValue(id, out var origin)
                    ? origin.SourceTimestamp
                    : string.Empty)
                .Where(date => !string.IsNullOrWhiteSpace(date))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return dates.Length switch
            {
                0 => string.Empty,
                1 => dates[0],
                // A learned item can be evidenced across several dates; collapsing that to one would
                // hide exactly the supersession a knowledge-update question turns on.
                _ => $"{dates[0]} .. {dates[^1]}"
            };
        }

        var builder = new StringBuilder("Retrieved memory:\n");
        foreach (var message in context.RelevantMessages.Items)
            AppendMessage(builder, message.Role, DisplayTimestamp(message), message.Content);
        foreach (var entity in context.RelevantEntities.Items)
        {
            builder.Append("[entity");
            if (SourceDates(entity.SourceMessageIds) is { Length: > 0 } entityDates)
                builder.Append(" @ ").Append(entityDates);
            builder.Append("] ").Append(entity.Name).Append(" (").Append(entity.Type).Append(')');
            if (!string.IsNullOrWhiteSpace(entity.Description))
                builder.Append(": ").Append(entity.Description);
            builder.AppendLine();
        }
        foreach (var fact in context.RelevantFacts.Items)
        {
            builder.Append("[fact");
            if (SourceDates(fact.SourceMessageIds) is { Length: > 0 } factDates)
                builder.Append(" @ ").Append(factDates);
            builder.Append("] ")
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
            builder.Append("[preference");
            if (SourceDates(preference.SourceMessageIds) is { Length: > 0 } preferenceDates)
                builder.Append(" @ ").Append(preferenceDates);
            builder.Append("] ").Append(preference.PreferenceText);
            if (!string.IsNullOrWhiteSpace(preference.Context))
                builder.Append(" (").Append(preference.Context).Append(')');
            builder.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(context.GraphRagContext))
            builder.Append("[graphrag]\n").AppendLine(context.GraphRagContext);
        return AppendQuestion(builder, question, currentDate);
    }

    private static void AppendMessage(
        StringBuilder builder, string role, string timestamp, string content)
    {
        builder.Append('[').Append(role);
        if (!string.IsNullOrWhiteSpace(timestamp))
            builder.Append(" @ ").Append(timestamp);
        builder.Append("] ").AppendLine(content);
    }

    private static string AppendQuestion(
        StringBuilder builder, string question, string? currentDate)
    {
        // Without "now", a relative-time question such as "how many days ago did I ..." is
        // unanswerable no matter how good retrieval was.
        if (!string.IsNullOrWhiteSpace(currentDate))
            builder.Append("\nCurrent date: ").AppendLine(currentDate);
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


    internal bool UseBatchedPreparation { get; init; }

    internal IMemoryExtractionPipeline? BatchExtractionPipeline { get; init; }

    internal IMultiSessionUnifiedMemoryExtractor? BatchPlanner { get; init; }

    internal int MaxSessionsPerBatch { get; init; } = 4;

    internal int MaxInputTokens { get; init; } = 100_000;

    internal int InitialQuestionNumber { get; init; }

    internal MultiSessionExtractionPlan? ExpectedExtractionPlan { get; init; }

    internal int? DiagnosticSourceSessionOrdinal { get; init; }
    /// <summary>
    /// Total non-GraphRAG answer-context item budget. Raw uses it entirely for messages; Structured
    /// divides it across entities/facts/preferences; Hybrid gives half to messages and divides the
    /// remainder across structured categories.
    /// </summary>

    public int MaxRelevantMessages { get; init; } = 30;

    /// <summary>
    /// G3B.1. When enabled, message recall over-fetches
    /// <see cref="SyntheticExclusionCandidateMultiplier"/>× the message budget, drops only the items
    /// AgentEval's formatter injected (session boundaries and padding), preserves the provider's
    /// retrieval order, and selects the first <see cref="MaxRelevantMessages"/> real source turns.
    /// </summary>
    /// <remarks>
    /// Default-off, because the raw arm is the immutable comparison control. In the accepted r8 run
    /// 240 of 300 final items were formatter boilerplate, and both questions reported as retrieval
    /// failures returned 30 of 30 — so the control never got the chance to rank a real turn. This
    /// changes selection only: storage, embeddings, the vector query and the item budget are
    /// untouched.
    /// </remarks>
    public bool ExcludeSyntheticFormatterMessages { get; init; }

    /// <summary>
    /// G3B.4. Presents the recalled messages in chronological order instead of similarity-rank
    /// order.
    /// </summary>
    /// <remarks>
    /// Retrieval rank answers "how relevant"; it says nothing about "when". A question such as
    /// "the order of the six museums I visited from earliest to latest" forces the reader to sort
    /// scattered dates itself. Selection is untouched - the same items in a different order - so this
    /// isolates presentation from retrieval.
    /// </remarks>
    public bool ChronologicalAnswerContext { get; init; }

    /// <summary>
    /// G3B.3. Maximum answer-context items any one source session may occupy. 0 disables the cap.
    /// </summary>
    /// <remarks>
    /// Measured motivation: on `gpt4_7abb270c` two sessions took 14 of 30 slots while a required
    /// sixth gold session — present in the candidate pool — received none. The cap reallocates slots
    /// only; the query, candidate pool, ranking and final item count are unchanged, and unused slots
    /// are refilled uncapped so the context is never left short.
    /// </remarks>
    public int MaxItemsPerSourceSession { get; init; }


    /// <summary>
    /// Recorded batch splits, used to decide whether excess provider calls are accounted for.
    /// </summary>
    /// <remarks>
    /// A split is a designed recovery that legitimately adds calls, so the cost guard needs to tell
    /// "the splitter ran" apart from "calls appeared that nobody can explain". Null means the
    /// harness has no split diagnostics wired, in which case any excess is treated as unexplained -
    /// failing closed rather than assuming innocence.
    /// </remarks>
    public Func<long>? BatchSplitCount { get; init; }

    /// <summary>
    /// K6. Adds a GraphRAG item budget on top of the mode's own budget.
    /// </summary>
    /// <remarks>
    /// Zero everywhere else, and zero by default here: every quality measurement this track has
    /// produced asked GraphRAG for nothing, so it has never been observed returning anything. This
    /// budget is <b>additive</b>, not a reallocation - the total context grows by up to this many
    /// items, so a score difference against a run without it is confounded with the larger budget
    /// and must not be read as GraphRAG's contribution. What the flag is for is the mechanism and
    /// the duplication rate, both of which are readable at any budget.
    /// </remarks>
    public int GraphRagItems { get; init; }

    /// <summary>G5. Returns every fact sharing a retrieved fact's canonical predicate.</summary>
    public bool ExpandFactsByPredicate { get; init; }

    /// <summary>J2.2. Also expands on relations resolved from the question text itself.</summary>
    public bool ResolveQueryRelations { get; init; }

    /// <summary>Cap on expanded facts.</summary>
    /// <remarks>
    /// Defaulted well below AgentEval's 100-reference evidence cap, which counts entities,
    /// facts <i>and</i> preferences — not the fact budget alone. A structured arm already spends ~30
    /// references before expansion adds any, so a 100-fact expansion guarantees the envelope
    /// overflows and the run is rejected mid-flight.
    /// </remarks>
    public int MaxExpandedFacts { get; init; } = 60;

    /// <summary>Candidate over-fetch factor used only when synthetic exclusion is enabled.</summary>
    /// <remarks>
    /// Raised from 3 to 5 by measurement: the first filtered run found formatter boilerplate still
    /// occupied <b>69% of the candidate pool at K = 90</b>, yielding ~27.9 real turns against a
    /// 30-item budget. Filling the budget needs ≈97 candidates, so 5× (150) leaves headroom rather
    /// than sitting on the boundary.
    /// </remarks>
    public int SyntheticExclusionCandidateMultiplier { get; init; } = 5;

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
    /// <summary>Length of the assembled answer prompt, the arm's actual context cost.</summary>
    public int AnswerPromptCharacters { get; init; }

    /// <summary>Approximate tokens in that prompt. An estimate, and named one.</summary>
    public int EstimatedContextTokens { get; init; }

    public string? QuestionId { get; init; }

    public LongMemEvalRetrievalEvidence? RetrievalEvidence { get; init; }

    public int ExtractionUnits { get; init; }

    public int MessagesPrepared { get; init; }

    public int ExtractionCallsPlanned { get; init; }

    public int ExtractionUnitsPrepared { get; init; }

    public bool PreparedMemory { get; init; }

    public int RawMessagesRetrieved { get; init; }

    public int EntitiesRetrieved { get; init; }

    public int FactsRetrieved { get; init; }

    public int PreferencesRetrieved { get; init; }

    public bool GraphRagIncluded { get; init; }

    /// <summary>
    /// Fraction of this question's gold source messages backed by a retrieved fact, or null when the
    /// question has no gold messages.
    /// </summary>
    public double? RetrievedGoldCoverage { get; init; }

    /// <summary>L2. Graph truth vs context for the relation(s) this question named.</summary>
    public LongMemEvalRelationCompleteness? RelationCompleteness { get; init; }

    /// <summary>Canonical relations this question resolved to; empty means expansion had nothing.</summary>
    public IReadOnlyList<string> ResolvedQueryRelations { get; init; } = Array.Empty<string>();

    /// <summary>K6. Passages GraphRAG actually returned.</summary>
    public int GraphRagItemsRetrieved { get; init; }

    /// <summary>K6. Of those, how many name a fact the structured surface already retrieved.</summary>
    public int GraphRagFactsAlreadyRetrieved { get; init; }

    public LongMemEvalGraphSnapshot? GraphReadBack { get; init; }

    /// <summary>
    /// G3B.5. Whether the cold build learned anything from the answer-bearing sessions. Null outside
    /// extraction modes. Zero <c>GoldLearnedItems</c> means Structured cannot answer this question at
    /// any recall quality — an extraction finding, never a retrieval one.
    /// </summary>
    public LongMemEvalGoldEvidenceCoverage? GoldEvidenceCoverage { get; init; }

    /// <summary>
    /// G3B.5 volume plausibility: learned items per contributing source message. A build that is
    /// sound and fully provenanced can still be far too thin, and "3 facts from 474 sessions" must
    /// be loud rather than silently green.
    /// </summary>
    public double LearnedItemsPerSourceMessage =>
        GraphReadBack is null || GraphReadBack.SourceMessages == 0
            ? 0d
            : (double)GraphReadBack.LearnedItems / GraphReadBack.SourceMessages;

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

    /// <summary>
    /// G3B.1. Keeps only real source turns from an over-fetched candidate set, in the provider's own
    /// retrieval order, then takes the first <paramref name="finalCap"/>.
    /// </summary>
    /// <remarks>
    /// Exclusion is driven solely by the formatter-supplied origin flags — no scoring, deduplication,
    /// diversity, recency, or second query. <c>RetrievalRank</c> is preserved exactly as the provider
    /// returned it so the candidate-set ceiling stays reportable; only <c>ContextRank</c> is renumbered
    /// over the survivors. An item with no known origin is kept: dropping what we cannot classify
    /// would silently shrink the budget.
    /// </remarks>
    internal static MemoryContextSection<Message> SelectRealSourceTurns(
        MemoryContextSection<Message> section,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> originsByMessageId,
        int finalCap,
        int maxPerSession = 0)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(originsByMessageId);
        ArgumentOutOfRangeException.ThrowIfNegative(finalCap);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPerSession);

        bool IsRealSourceTurn(string messageId) =>
            !originsByMessageId.TryGetValue(messageId, out var origin) ||
            (!origin.IsSyntheticBoundary && !origin.IsSyntheticFormatterPadding);

        /// <summary>
        /// G3B.3. Fills the budget in ranked order while no source session exceeds
        /// <paramref name="maxPerSession"/>, then refills any unused slots from the skipped items,
        /// uncapped, so the context is never left short. An item with no known origin is keyed by its
        /// own id and therefore never capped.
        /// </summary>
        string[] Diversify(string[] candidates)
        {
            if (maxPerSession == 0 || candidates.Length <= finalCap)
                return candidates.Take(finalCap).ToArray();

            var perSession = new Dictionary<string, int>(StringComparer.Ordinal);
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var skipped = new List<string>();
            foreach (var id in candidates)
            {
                if (selected.Count == finalCap) break;
                var session = originsByMessageId.TryGetValue(id, out var origin)
                    ? origin.SourceSessionId
                    : id;
                var taken = perSession.GetValueOrDefault(session);
                if (taken >= maxPerSession)
                {
                    skipped.Add(id);
                    continue;
                }

                perSession[session] = taken + 1;
                selected.Add(id);
            }

            foreach (var id in skipped)
            {
                if (selected.Count == finalCap) break;
                selected.Add(id);
            }

            // Emit in the provider's own retrieval order, not selection order.
            return candidates.Where(selected.Contains).ToArray();
        }

        var ranked = section.RankedItems.Count > 0
            ? section.RankedItems.OrderBy(item => item.ContextRank).ToArray()
            : [];
        if (ranked.Length == 0)
        {
            // No diagnostics were requested, so retrieval order is only observable through Items.
            return section with
            {
                Items = Diversify(
                        section.Items.Where(m => IsRealSourceTurn(m.MessageId))
                            .Select(m => m.MessageId).ToArray())
                    .Select(id => section.Items.First(m => m.MessageId == id))
                    .ToArray()
            };
        }

        var itemsById = section.Items.ToDictionary(m => m.MessageId, StringComparer.Ordinal);
        var keptIds = Diversify(ranked
            .Select(item => item.ItemId)
            .Where(IsRealSourceTurn)
            .Where(itemsById.ContainsKey)
            .ToArray());
        var keptSet = keptIds.ToHashSet(StringComparer.Ordinal);

        return section with
        {
            Items = keptIds.Select(id => itemsById[id]).ToArray(),
            RankedItems = ranked
                .Where(item => keptSet.Contains(item.ItemId))
                .Select((item, index) => item with { ContextRank = index + 1 })
                .ToArray()
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

/// <summary>L2. Relation completeness for one question: graph truth vs what reached the context.</summary>
public sealed record LongMemEvalRelationCompleteness
{
    /// <summary>The stored predicate keys expansion would have searched, widened from the question.</summary>
    public IReadOnlyList<string> StoredPredicateKeys { get; init; } = Array.Empty<string>();

    /// <summary>Per-key graph counts, reported raw so a partial miss is attributable to a key.</summary>
    public IReadOnlyDictionary<string, int> PerKeyGraphCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Live facts in the graph under those keys. Null when not measured.</summary>
    public int? Denominator { get; init; }

    /// <summary>Distinct facts in the context under those keys. Null when not measured.</summary>
    public int? Numerator { get; init; }

    /// <summary>Numerator / Denominator, or null when there is nothing to divide.</summary>
    public double? Ratio { get; init; }

    /// <summary>Whether the context held every one. Null when not measured or nothing to hold.</summary>
    public bool? Complete { get; init; }

    /// <summary>The relation resolved but the graph holds none of it — an EXTRACTION miss.</summary>
    public bool RelationAbsentFromGraph { get; init; }

    /// <summary>
    /// Whether the shared expansion budget was exhausted by the whole predicate union. Null when the
    /// union total was not measured — never <see langword="false"/> by default.
    /// </summary>
    public bool? LimitBinding { get; init; }

    /// <summary>Live facts under the ENTIRE predicate union the expansion query searched.</summary>
    public int? UnionGraphTotal { get; init; }

    /// <summary>The MaxExpandedFacts in force, recorded so LimitBinding is checkable.</summary>
    public int ExpansionLimit { get; init; }
}
