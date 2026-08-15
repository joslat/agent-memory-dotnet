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
    ITimestampedHistoryInjectableAgent,
    ISessionResettableAgent
{
    internal const string SystemPrompt =
        "Answer the question using only the retrieved memory below. " +
        "Be concise and do not claim information that is absent from memory.";

    private readonly IMemoryService _memory;
    private readonly IChatClient _chatClient;
    private readonly string _runId;
    private readonly LongMemEvalAdapterOptions _options;

    /// <summary>
    /// Tokenizer for the B3 breakdown, built lazily from the answer model.
    /// </summary>
    /// <remarks>
    /// Lazy because constructing a tiktoken encoding is not free and a run that never reaches a
    /// successful recall never needs one.
    /// </remarks>
    private LongMemEvalTokenCounter? _tokenCounter;

    private LongMemEvalTokenCounter TokenCounter =>
        _tokenCounter ??= new LongMemEvalTokenCounter(_options.ModelId);
    private readonly object _stateLock = new();
    private readonly List<LongMemEvalQuestionTelemetry> _telemetry = [];
    private IReadOnlyList<(string UserMessage, string AssistantResponse)>? _pendingHistory;

    /// <summary>
    /// One instant per pending turn pair, aligned with <see cref="_pendingHistory"/>. Null when the
    /// history arrived through the untimestamped channel.
    /// </summary>
    private IReadOnlyList<DateTimeOffset>? _pendingTurnTimestamps;

    /// <summary>
    /// The instant the pending question is asked, from the typed channel. Null when the history
    /// arrived untimestamped.
    /// </summary>
    private DateTimeOffset? _pendingQueryTime;
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

    /// <summary>
    /// Accepts history whose turns carry real instants (TypedMemEval's TimestampsOnly channel).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each turn's <see cref="TimestampedConversationTurn.Timestamp"/> becomes the stored messages'
    /// own clock (<c>Message.TimestampUtc</c> — the product's valid time), replacing the epoch +
    /// injection-ordinal clock the untimestamped channel uses. <b>No date text is added to any
    /// message content</b>: the whole point of the TimestampsOnly grounding mode is that dates are
    /// structural, and a harness that re-printed them into the text would hand back the crutch the
    /// mode exists to remove.
    /// </para>
    /// <para>
    /// <see cref="TimestampedConversationHistory.QueryTime"/> is carried to the question call as the
    /// answer-time anchor: recall runs through <c>RecallAsOfAsync</c> with QueryTime as the
    /// valid-time clock (so a fact's validity window is judged against the question's "now", not the
    /// machine's), and the answer prompt's "Current date" line is rendered from it rather than from
    /// evaluator-side knowledge.
    /// </para>
    /// <para>
    /// Refused up front when the adapter options include surfaces the point-in-time recall path does
    /// not implement (predicate expansion, query-relation resolution, GraphRAG): accepting them
    /// would run a question whose configuration silently did nothing — the dead-option shape this
    /// codebase exists to refuse.
    /// </para>
    /// </remarks>
    public void InjectTimestampedConversationHistory(TimestampedConversationHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(history.Turns);
        if (_options.ExpandFactsByPredicate || _options.ResolveQueryRelations ||
            _options.GraphRagItems > 0)
        {
            throw new InvalidOperationException(
                "Timestamped LongMemEval history anchors recall at RecallAsOfAsync, which does not " +
                "implement predicate expansion, query-relation resolution, or GraphRAG; refusing to " +
                "run a question whose options would be silently ignored.");
        }

        var pairs = new (string UserMessage, string AssistantResponse)[history.Turns.Count];
        var timestamps = new DateTimeOffset[history.Turns.Count];
        for (var index = 0; index < history.Turns.Count; index++)
        {
            var turn = history.Turns[index];
            pairs[index] = (turn.UserMessage, turn.AssistantResponse);
            timestamps[index] = turn.Timestamp;
        }

        lock (_stateLock)
        {
            if (_pendingHistory is not null)
            {
                throw new InvalidOperationException(
                    "LongMemEval history was injected more than once for the same question.");
            }

            _pendingHistory = pairs;
            _pendingTurnTimestamps = timestamps;
            _pendingQueryTime = history.QueryTime;
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
            _pendingTurnTimestamps = null;
            _pendingQueryTime = null;
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
        IReadOnlyList<DateTimeOffset>? turnTimestamps;
        DateTimeOffset? queryTime;
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

            turnTimestamps = _pendingTurnTimestamps;
            queryTime = _pendingQueryTime;
            _pendingHistory = null;
            _pendingTurnTimestamps = null;
            _pendingQueryTime = null;
            sessionId = _sessionId;
            ownerId = _ownerId;
            questionNumber = _questionNumber;
        }

        LongMemEvalEvidenceQuestion? evidenceQuestion = null;
        try
        {
            // A timestamped question must resolve through the QueryTime-aware fingerprint:
            // Prospective pair arms share one haystack and differ only in the query instant, so a
            // fingerprint over the turns alone would make the two arms indistinguishable.
            if (_options.EvidenceIndex is not null)
                evidenceQuestion = queryTime is { } evidenceQueryTime
                    ? _options.EvidenceIndex.Resolve(history, evidenceQueryTime, prompt)
                    : _options.EvidenceIndex.Resolve(history, prompt);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTelemetry(questionNumber, 0, 0, false, "evidence-resolution-error");
            throw;
        }

        var originsByMessageId = new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal);
        var messages = BuildMessages(
            _runId, history, sessionId, ownerId, questionNumber, evidenceQuestion, originsByMessageId,
            turnTimestamps);

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
        // Set when retrieval returned nothing against a graph proven populated; carried to the
        // single completion record so the outcome is scored without being recorded twice.
        var retrievalEmpty = false;
        // Distinguishes "nothing at all" from "items, but zero learned ones" in a memory-only arm.
        // Both are scored; keeping them apart keeps the report able to say which happened.
        var retrievalStructuredEmpty = false;
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
        var recallRequest = new RecallRequest
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
        };
        RecallResult recall;
        try
        {
            recall = await timings.MeasureAsync(
                LongMemEvalStage.Retrieval,
                () => LongMemEvalRuntime.ExecuteStageAsync(
                    "retrieval",
                    // A timestamped question anchors recall at its own "now". QueryTime is the
                    // VALID-time clock — it decides which facts' validity windows contain the
                    // question's instant, which is precisely the prospective-memory semantics the
                    // typed channel exists to measure. The TRANSACTION clock must stay at the
                    // machine's now: the corpus was ingested moments ago, so bounding created_at at
                    // a 2023 QueryTime would erase everything just stored and score an empty memory.
                    () => queryTime is { } asOf
                        ? _memory.RecallAsOfAsync(
                            recallRequest, asOf, DateTimeOffset.UtcNow, cancellationToken)
                        : _memory.RecallAsync(recallRequest, cancellationToken))).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RecordTelemetry(questionNumber, messages.Count, 0, false, "retrieval-exception");
            throw;
        }

        if (recall.TotalItemsRetrieved == 0)
        {
            if (!CanScoreEmptyRetrieval(graphSnapshot))
            {
                // Unmeasurable: record and stop. Full telemetry, not the five-positional-argument
                // call this used to make - that left nine optional parameters at their defaults, so
                // the report showed MessagesStored=<built count>, PreparedMemory=false and
                // QuestionId=null for a question that had used prepared memory throughout. Defaults
                // presented as measurements; it sent the first diagnosis of this failure the wrong
                // way entirely.
                RecordTelemetry(
                    questionNumber,
                    messagesStored,
                    0,
                    recall.Truncated,
                    "retrieval-empty",
                    evidenceQuestion?.QuestionId,
                    extractionUnits: extractionUnits,
                    context: recall.Context,
                    graphSnapshot: graphSnapshot,
                    stageTimings: timings.Snapshot(),
                    messagesPrepared: preparedQuestion?.MessagesPrepared ?? 0,
                    preparedMemory: _options.PreparedMemory,
                    extractionCallsPlanned: extractionCallsPlanned);

                throw new InvalidOperationException(
                    $"AgentMemory retrieved no history for LongMemEval question {questionNumber}, and " +
                    "the graph read-back does not prove its memory was populated with complete " +
                    "provenance; refusing to manufacture a score.");
            }

            // The graph IS proven populated, so this is a genuine retrieval failure and it gets
            // scored as one: the answer call proceeds against an empty memory context and will
            // almost certainly be judged wrong, which is the truthful outcome for a memory system
            // that returned nothing, and it keeps the arms paired so the comparison stays valid.
            //
            // Deliberately NOT recorded here. This question continues to the single completion
            // record at the end of the method, and recording twice produced two telemetry entries
            // for one question - which surfaced as "An item with the same key has already been
            // added. Key: 32260d93" and killed the first acceptance run. The status is carried
            // instead.
            retrievalEmpty = true;
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
            // The same judgement as the total-empty case above, for the memory-only arm: items came
            // back but zero LEARNED ones. With the graph PROVEN populated with complete provenance
            // this is a measured retrieval failure and is scored as one; without that proof it stays
            // unmeasurable and still throws.
            if (!CanScoreEmptyRetrieval(graphSnapshot))
            {
                RecordTelemetry(
                    questionNumber,
                    messagesStored,
                    recall.TotalItemsRetrieved,
                    recall.Truncated,
                    "retrieval-structured-empty",
                    evidenceQuestion?.QuestionId,
                    extractionUnits: extractionUnits,
                    context: recall.Context,
                    graphSnapshot: graphSnapshot,
                    stageTimings: timings.Snapshot(),
                    messagesPrepared: preparedQuestion?.MessagesPrepared ?? 0,
                    preparedMemory: _options.PreparedMemory,
                    extractionCallsPlanned: extractionCallsPlanned);
                throw new InvalidOperationException(
                    $"AgentMemory retrieved no structured memory for LongMemEval question {questionNumber}, " +
                    "and the graph read-back does not prove its memory was populated with complete " +
                    "provenance; refusing to manufacture a score.");
            }

            // Carried to the single completion record. Recording here and continuing would
            // double-record the question — the defect that killed the previous acceptance run.
            retrievalEmpty = true;
            retrievalStructuredEmpty = true;
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

        // The answer-time anchor. A timestamped question renders "now" from the QueryTime the typed
        // channel delivered — data the system under test legitimately holds — rather than from the
        // evaluator-side index, which the untimestamped path (whose only source is the evidence
        // index) still uses.
        var answerPrompt = BuildAnswerPrompt(
            recall.Context,
            prompt,
            queryTime is { } answerNow ? FormatQueryTime(answerNow) : evidenceQuestion?.QuestionDate,
            originsByMessageId);
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
        // The answer-presence gate. Deliberately reads the OWNER'S WHOLE MEMORY rather than what
        // retrieval returned: checking the retrieved set would conflate the two failures this exists
        // to tell apart. Null when no probe is wired — "not measured", never "absent".
        LongMemEvalAnswerPresenceResult? answerPresence = null;
        if (_options.GraphProbe is not null && evidenceQuestion is not null)
        {
            var memoryText = await _options.GraphProbe
                .ReadMemoryTextAsync(ownerId, cancellationToken)
                .ConfigureAwait(false);
            answerPresence = LongMemEvalAnswerPresence.Evaluate(evidenceQuestion.GoldAnswer, memoryText);
        }

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
            retrievalStructuredEmpty ? "retrieval-structured-empty"
                : retrievalEmpty ? "retrieval-empty"
                : "completed",
            evidenceQuestion?.QuestionId,
            retrievalEvidence,
            extractionUnits,
            recall.Context,
            // B3: the transcript this context was distilled from, so the compression ratio has a
            // denominator. Named from here on -- the positional tail is what a new parameter breaks.
            fullHistory: messages,
            graphSnapshot: graphSnapshot,
            stageTimings: timings.Snapshot(),
            messagesPrepared: preparedQuestion?.MessagesPrepared ?? 0,
            extractionUnitsPrepared: preparedQuestion?.ExtractionUnitsPrepared ?? 0,
            preparedMemory: preparedQuestion is not null,
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
            answerPresence: answerPresence,
            questionType: evidenceQuestion?.QuestionType,
            isAbstention: evidenceQuestion?.IsAbstention ?? false,
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

    /// <summary>
    /// Whether an empty retrieval is a measurable result rather than a broken run.
    /// </summary>
    /// <remarks>
    /// Refusing to score an empty retrieval is right when it means the harness malfunctioned. It is
    /// wrong when the memory system simply returned nothing: the n=50 rebuild of 2026-08-10 hit a
    /// question that retrieved zero from a graph the read-back had already proven held 504 facts,
    /// 346 entities and 1,336 learned items with complete provenance, whose gold evidence was
    /// demonstrably learned, and whose raw-message search ranked the gold turn first. Nothing was
    /// broken. The honest score for that is <i>wrong</i>, not <i>unmeasurable</i>.
    /// <para>
    /// Throwing cost the whole run: the structured arm produced 49 answers to hybrid's 50, so the
    /// pair was unpaired and the comparison was rejected — discarding 83 minutes of extraction over
    /// the single question that most sharply discriminates the two arms.
    /// </para>
    /// <para>
    /// This <b>refines</b> the guard rather than weakening it. The graph read-back runs earlier and
    /// already throws when it cannot prove the memory is populated, so "populated with complete
    /// provenance" is exactly the evidence separating a retrieval failure from a preparation
    /// failure. With no snapshot there is no proof, the two are indistinguishable, and the guard
    /// must still fire — guessing is what it exists to prevent.
    /// </para>
    /// </remarks>
    internal static bool CanScoreEmptyRetrieval(LongMemEvalGraphSnapshot? graphSnapshot) =>
        graphSnapshot is { TotalLearned: > 0, CompleteProvenance: true };

    /// <summary>
    /// The strongest similarity any searched section achieved, or null when nothing was searched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Max across sections rather than facts alone: an answer can arrive through any tier, so a
    /// fact-only signal would score a question answered from a retrieved message as unsupported.
    /// </para>
    /// <para>
    /// A section that was <b>searched and came back empty</b> contributes its own minimum-score floor,
    /// not zero and not nothing. It is evidence — "we looked at this threshold and found nothing
    /// above it" — and dropping it would remove exactly the observations an abstention policy exists
    /// to act on, leaving the AUC measured only over questions where retrieval already succeeded.
    /// </para>
    /// <para>
    /// Null when <c>IncludeDiagnostics</c> was off, never 0: a signal that was not collected must not
    /// be indistinguishable from one that scored badly.
    /// </para>
    /// </remarks>
    private static double? SufficiencySignalOf(MemoryContext? context)
    {
        if (context is null) return null;

        double? best = null;
        void Consider(MemoryContextSectionDiagnostics? diagnostics)
        {
            if (diagnostics is not { Searched: true }) return;
            var score = diagnostics.TopScore ?? diagnostics.MinimumScore;
            if (best is null || score > best) best = score;
        }

        Consider(context.RelevantMessages.Diagnostics);
        Consider(context.RelevantEntities.Diagnostics);
        Consider(context.RelevantPreferences.Diagnostics);
        Consider(context.RelevantFacts.Diagnostics);
        Consider(context.SimilarTraces.Diagnostics);
        return best;
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
        IReadOnlyList<Message>? fullHistory = null,
        LongMemEvalGraphSnapshot? graphSnapshot = null,
        LongMemEvalStageTimings? stageTimings = null,
        int messagesPrepared = 0,
        int extractionUnitsPrepared = 0,
        bool preparedMemory = false,
        int extractionCallsPlanned = 0,
        LongMemEvalGoldEvidenceCoverage? goldCoverage = null,
        double? retrievedGoldCoverage = null,
        LongMemEvalRelationCompleteness? relationCompleteness = null,
        LongMemEvalAnswerPresenceResult? answerPresence = null,
        string? questionType = null,
        bool isAbstention = false,
        string? answerPromptText = null)
    {
        lock (_stateLock)
        {
            _telemetry.Add(new LongMemEvalQuestionTelemetry(
                questionNumber, messagesStored, itemsRetrieved, recallTruncated, status)
            {
                QuestionId = questionId,
                // B3. Measured only when both halves are present: a breakdown against an absent
                // transcript would report a null ratio, which is an absent measurement rather than a
                // flattering one -- but emitting the row at all would invite it being read as zero.
                TokenBreakdown = context is not null && fullHistory is { Count: > 0 }
                    ? ContextTokenBreakdown.Measure(context, fullHistory, TokenCounter)
                    : null,
                RetrievalEvidence = retrievalEvidence,
                ExtractionUnits = extractionUnits,
                MessagesPrepared = messagesPrepared,
                ExtractionUnitsPrepared = extractionUnitsPrepared,
                PreparedMemory = preparedMemory,
                RawMessagesRetrieved = context?.RelevantMessages.Items.Count ?? 0,
                EntitiesRetrieved = context?.RelevantEntities.Items.Count ?? 0,
                ExtractionCallsPlanned = extractionCallsPlanned,
                FactsRetrieved = context?.RelevantFacts.Items.Count ?? 0,
                EpisodicFactsRetrieved = CountEpisodicFacts(context),
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
                AnswerPresence = answerPresence,
                // 4.2. The scalar the abstention story rests on: how confident retrieval was that it
                // found anything. Paired against AnswerPresence at report time to ask whether the
                // signal ORDERS answerable above unanswerable at all -- the question no calibration
                // work can skip and none of it has ever asked.
                SufficiencySignal = SufficiencySignalOf(context),
                QuestionType = questionType,
                IsAbstention = isAbstention,
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
        IDictionary<string, LongMemEvalMessageOrigin> originsByMessageId,
        IReadOnlyList<DateTimeOffset>? turnTimestamps = null)
    {
        var expectedCount = history.Count * 2;
        if (evidenceQuestion is not null && evidenceQuestion.Messages.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"LongMemEval evidence contained {evidenceQuestion.Messages.Count} origins for {expectedCount} injected messages.");
        }
        if (turnTimestamps is not null && turnTimestamps.Count != history.Count)
        {
            throw new InvalidOperationException(
                $"LongMemEval timestamped history carried {turnTimestamps.Count} instants for {history.Count} turns.");
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
                // The typed channel's whole claim: the turn's own instant becomes the message's
                // valid time. Both halves of a pair share the turn's instant, and the untimestamped
                // channel keeps its epoch + injection-ordinal clock so every sealed measurement
                // stays byte-comparable with itself.
                TimestampUtc = turnTimestamps is null
                    ? DateTimeOffset.UnixEpoch.AddSeconds(current)
                    : turnTimestamps[current / 2],
                Metadata = metadata
            };
        }
    }

    /// <summary>
    /// Renders a QueryTime for the answer prompt's "Current date" line, in the corpus's own date
    /// style so the anchor and the per-message timestamps read as one convention.
    /// </summary>
    internal static string FormatQueryTime(DateTimeOffset queryTime) =>
        queryTime.UtcDateTime.ToString(
            "yyyy/MM/dd (ddd) HH:mm", System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>
    /// The canonical subject under which episodic facts are written — see
    /// <c>ExtractionPromptSemantics</c>, which instructs the extractor to use the assistant as the
    /// subject for what the assistant did.
    /// </summary>
    private static readonly string EpisodicSubjectKey =
        MemoryTripleCanonicalizer.CanonicalValue("assistant");

    /// <summary>
    /// Counts retrieved facts whose subject is the assistant — the episodic half of memory.
    /// </summary>
    /// <remarks>
    /// The subject is canonicalized rather than compared with <c>==</c>, so "Assistant" and
    /// "assistant " are the same subject here for exactly the reason they are the same subject to the
    /// write path's MERGE key. Comparing raw strings would undercount by whatever the extractor
    /// happened to capitalise.
    /// </remarks>
    private static int CountEpisodicFacts(MemoryContext? context)
    {
        if (context is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var fact in context.RelevantFacts.Items)
        {
            if (string.Equals(
                    MemoryTripleCanonicalizer.CanonicalValue(fact.Subject),
                    EpisodicSubjectKey,
                    StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
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

    /// <summary>
    /// B3. The real token cost of the assembled memory context against the full transcript.
    /// </summary>
    /// <remarks>
    /// Sits beside <see cref="EstimatedContextTokens"/> rather than replacing it, so the chars/4
    /// estimate every earlier run recorded stays comparable with itself. The published claim —
    /// "answering from memory costs a fraction of sending the conversation" — is read off this one,
    /// because a compression ratio derived from an estimate is a claim about arithmetic.
    /// </remarks>
    public ContextTokenBreakdown? TokenBreakdown { get; init; }

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

    /// <summary>
    /// How many of <see cref="FactsRetrieved"/> are EPISODIC — a record of what the assistant did in
    /// the conversation, rather than a claim about the world.
    /// </summary>
    /// <remarks>
    /// Episodic CAPTURE is measured (0 → 3,048 relations); its RETRIEVAL half never was, because
    /// nothing recorded <i>which</i> facts came back. <see cref="FactsRetrieved"/> says 38 without
    /// saying whether any of them was episodic, and <c>RetrievalEvidence.RankedItems</c> is empty on
    /// the prepared path — so "is episodic memory ever retrieved" had no instrument, which is why it
    /// stayed unmeasured rather than measured-and-inconclusive.
    /// <para>
    /// Identified by SUBJECT. The extraction prompt writes these with the assistant as the subject,
    /// so the subject is the marker; the predicate vocabulary (recommended/told/provided/suggested/
    /// explained) is deliberately not relied on, since the model may reach for a verb outside it.
    /// </para>
    /// <para>
    /// <b>A heuristic, and its false-positive rate is measured rather than assumed.</b> A user turn
    /// genuinely about "the assistant" is counted here too. Probing both frozen bases directly:
    /// <list type="bullet">
    /// <item><description><b>Ignore</b> base — 17 of 25,668 facts, <b>0.07%</b>.</description></item>
    /// <item><description><b>Utterance</b> base — 13,251 of 36,489 facts, <b>36.3%</b>.</description></item>
    /// </list>
    /// So the floor is 0.07%, <i>not</i> zero — an earlier draft of this comment asserted it must be
    /// exactly 0, which the probe disproved. Against a 36.3% signal a 0.07% floor cannot manufacture
    /// the result, which is what makes the marker usable; a count near the floor in Utterance mode
    /// would indict this counter rather than reveal anything about memory.
    /// </para>
    /// </remarks>
    public int EpisodicFactsRetrieved { get; init; }

    public int PreferencesRetrieved { get; init; }

    public bool GraphRagIncluded { get; init; }

    /// <summary>
    /// Fraction of this question's gold source messages backed by a retrieved fact, or null when the
    /// question has no gold messages.
    /// </summary>
    public double? RetrievedGoldCoverage { get; init; }

    /// <summary>L2. Graph truth vs context for the relation(s) this question named.</summary>
    public LongMemEvalRelationCompleteness? RelationCompleteness { get; init; }

    /// <summary>
    /// Whether the gold answer is present in this owner's stored memory <b>at all</b>.
    /// </summary>
    /// <remarks>
    /// The one question no other field answers. Relation completeness, gold coverage and recall@K all
    /// ask "did retrieval find what was stored"; this asks whether the answer was ever stored, which
    /// is what separates an <b>extraction</b> failure from a <b>retrieval</b> failure. Null means the
    /// graph probe was not wired — never "absent", and never "fine".
    /// </remarks>
    public LongMemEvalAnswerPresenceResult? AnswerPresence { get; init; }

    /// <summary>
    /// How confident retrieval was that it found anything: the strongest similarity any searched
    /// section achieved, with a searched-but-empty section contributing its threshold floor.
    /// </summary>
    /// <remarks>
    /// Null means diagnostics were not collected — never "scored zero". Paired against
    /// <see cref="AnswerPresence"/> it answers PLAN 4.2: does this signal order answerable questions
    /// above unanswerable ones? An AUC near 0.5 kills every abstention and calibration story built on
    /// it, which is why it is worth measuring before any of them is built.
    /// </remarks>
    public double? SufficiencySignal { get; init; }

    /// <summary>
    /// Whether the dataset declares this question <b>unanswerable</b> (an <c>_abs</c> question).
    /// </summary>
    /// <remarks>
    /// Ground truth, and the reason it is carried separately from
    /// <see cref="AnswerPresence"/>. The presence gate asks whether the gold answer's distinctive
    /// tokens appear anywhere in memory -- a deliberately cheap floor for spotting extraction
    /// failure. Measured on an abstention-enriched sample, it reported <b>3 of 4</b> abstention
    /// questions as "present": their topic is discussed in the conversation even though the specific
    /// fact is not, so the tokens are there. For "was this answerable?" the dataset's own label is
    /// correct and the heuristic is not.
    /// </remarks>
    public bool IsAbstention { get; init; }

    /// <summary>
    /// The benchmark's own question type, carried so the gate can be read per type.
    /// </summary>
    /// <remarks>
    /// It lives on the evidence question and on AgentEval's result, but never reached the record that
    /// holds the gate verdict — so "absent because not stored" and "absent because the answer is
    /// computed from what IS stored" could not be told apart.
    /// </remarks>
    public string? QuestionType { get; init; }

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
