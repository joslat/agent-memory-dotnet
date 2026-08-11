using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.Llm;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.LongMemEval;

internal static class LongMemEvalPreparedBatchExecutor
{
    internal static IReadOnlyList<int> SelectCheckpointQuestionIndexes(
        IReadOnlyList<MultiSessionExtractionPlan> plans,
        int checkpointQuestions)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (checkpointQuestions <= 0 || checkpointQuestions > plans.Count)
            throw new ArgumentOutOfRangeException(nameof(checkpointQuestions));

        return Enumerable.Range(0, plans.Count)
            .OrderByDescending(index => plans[index].TotalEstimatedInputTokens)
            .ThenBy(index => index)
            .Take(checkpointQuestions)
            .OrderBy(index => index)
            .ToArray();
    }

    internal static double ProjectFullPreparationMilliseconds(
        long fullCalls,
        long fullSourceSessions,
        long fullEstimatedInputTokens,
        long checkpointCalls,
        long checkpointSourceSessions,
        long checkpointEstimatedInputTokens,
        double checkpointWallMilliseconds,
        double profileStartupMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fullCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fullSourceSessions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fullEstimatedInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointSourceSessions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointEstimatedInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointWallMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(profileStartupMilliseconds);

        var scale = Math.Max(
            (double)fullCalls / checkpointCalls,
            Math.Max(
                (double)fullSourceSessions / checkpointSourceSessions,
                (double)fullEstimatedInputTokens / checkpointEstimatedInputTokens));
        return profileStartupMilliseconds + (1.25d * checkpointWallMilliseconds * scale);
    }

    internal static IReadOnlyList<MultiSessionExtractionPlan> Preflight(
        IServiceProvider services,
        string preparationId,
        LongMemEvalEvidenceIndex evidenceIndex,
        IReadOnlyList<LongMemEvalEvidenceQuestion> questions,
        int maxSessionsPerBatch,
        int maxInputTokens)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        ArgumentNullException.ThrowIfNull(evidenceIndex);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputTokens);

        var planner = RequirePlanner(services);
        var plans = new MultiSessionExtractionPlan[questions.Count];
        for (var index = 0; index < questions.Count; index++)
        {
            var questionNumber = index + 1;
            var question = questions[index];
            var history = LongMemEvalBenchmarkProtocol.History(question);
            var origins = new Dictionary<string, LongMemEvalMessageOrigin>(
                StringComparer.Ordinal);
            var messages = AgentMemoryLongMemEvalAdapter.BuildMessages(
                preparationId,
                history,
                ScopeId(preparationId, "session", questionNumber),
                ScopeId(preparationId, "owner", questionNumber),
                questionNumber,
                question,
                origins);
            var requests = AgentMemoryLongMemEvalAdapter.BuildExtractionRequests(
                messages,
                question,
                ScopeId(preparationId, "session", questionNumber),
                ScopeId(preparationId, "owner", questionNumber));
            var plan = planner.Plan(
                requests,
                maxSessionsPerBatch,
                maxInputTokens);
            if (plan.SourceSessionCount != requests.Count ||
                plan.BatchCount <= 0 ||
                plan.Batches.Any(batch =>
                    batch.SourceSessionIds.Count == 0 ||
                    batch.SourceSessionIds.Count > maxSessionsPerBatch ||
                    batch.EstimatedInputTokens <= 0 ||
                    batch.EstimatedInputTokens > maxInputTokens))
            {
                throw new InvalidOperationException(
                    $"LongMemEval question {questionNumber} produced an invalid preflight batch plan.");
            }

            plans[index] = plan;
        }

        return plans;
    }

    internal static async Task<LongMemEvalPreparedBatchExecution> ExecuteAsync(
        IServiceProvider services,
        LongMemEvalChatCallMeter extractionCalls,
        string preparationId,
        LongMemEvalEvidenceIndex evidenceIndex,
        IReadOnlyList<LongMemEvalEvidenceQuestion> questions,
        IReadOnlyList<MultiSessionExtractionPlan> plans,
        string modelId,
        LongMemEvalEvidenceDetail evidenceDetail,
        int maxRelevantMessages,
        int preparationWorkers,
        int maxSessionsPerBatch,
        int maxInputTokens,
        IReadOnlyList<int>? questionIndexes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(extractionCalls);
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        ArgumentNullException.ThrowIfNull(evidenceIndex);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparationWorkers);
        if (questions.Count != plans.Count)
            throw new ArgumentException("Every LongMemEval question requires one frozen batch plan.");

        var executionIndexes = questionIndexes?.ToArray() ??
            Enumerable.Range(0, questions.Count).ToArray();
        if (executionIndexes.Length == 0 ||
            executionIndexes.Distinct().Count() != executionIndexes.Length ||
            executionIndexes.Any(index => index < 0 || index >= questions.Count))
        {
            throw new ArgumentException(
                "Checkpoint question indexes must be nonempty, unique, and in range.",
                nameof(questionIndexes));
        }

        var telemetry = new LongMemEvalQuestionTelemetry[executionIndexes.Length];
        var active = 0;
        var maximumActive = 0;
        var completed = 0;
        var driver = services.GetRequiredService<IDriver>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, executionIndexes.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = preparationWorkers,
                CancellationToken = cancellationToken
            },
            async (executionPosition, itemCancellationToken) =>
            {
                var index = executionIndexes[executionPosition];
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                try
                {
                    await using var scope = services.CreateAsyncScope();
                    var scoped = scope.ServiceProvider;
                    var planner = RequirePlanner(scoped);
                    var adapter = new AgentMemoryLongMemEvalAdapter(
                        scoped.GetRequiredService<IMemoryService>(),
                        extractionCalls,
                        preparationId,
                        new LongMemEvalAdapterOptions
                        {
                            MemoryMode = LongMemEvalMemoryMode.Structured,
                            MaxRelevantMessages = maxRelevantMessages,
                            MinSimilarityScore = 0,
                            ModelId = modelId,
                            EvidenceIndex = evidenceIndex,
                            EvidenceDetail = evidenceDetail,
                            RequireGraphReadBack = true,
                            GraphProbe = new Neo4jLongMemEvalGraphProbe(driver),
                            PreparationOnly = true,
                            UseBatchedPreparation = true,
                            BatchExtractionPipeline =
                                scoped.GetRequiredService<IMemoryExtractionPipeline>(),
                            // Lets the cost guard tell a designed recovery apart from unexplained
                            // work: a split legitimately adds provider calls, so excess calls are
                            // acceptable only when the splitter actually ran.
                            BatchSplitCount = () => scoped
                                .GetRequiredService<LlmExtractionBatchDiagnostics>()
                                .Snapshot().Splits,
                            BatchPlanner = planner,
                            MaxSessionsPerBatch = maxSessionsPerBatch,
                            MaxInputTokens = maxInputTokens,
                            InitialQuestionNumber = index,
                            ExpectedExtractionPlan = plans[index]
                        });

                    await adapter.ResetSessionAsync(itemCancellationToken)
                        .ConfigureAwait(false);
                    adapter.InjectConversationHistory(
                        LongMemEvalBenchmarkProtocol.History(questions[index]));
                    _ = await adapter.InvokeAsync(
                            questions[index].InvocationPrompt,
                            itemCancellationToken)
                        .ConfigureAwait(false);
                    var questionTelemetry = adapter.QuestionTelemetry;
                    if (questionTelemetry.Count != 1 ||
                        questionTelemetry[0].QuestionNumber != index + 1 ||
                        questionTelemetry[0].ExtractionCallsPlanned !=
                        plans[index].BatchCount)
                    {
                        throw new InvalidOperationException(
                            $"LongMemEval question {index + 1} did not record its exact frozen batch plan.");
                    }

                    telemetry[executionPosition] = questionTelemetry[0];
                    var completedNow = Interlocked.Increment(ref completed);
                    Console.WriteLine(
                        $"longmemeval: prepared question {completedNow}/{executionIndexes.Length} " +
                        $"(source question {index + 1}).");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }).ConfigureAwait(false);

        if (telemetry.Any(item => item is null))
            throw new InvalidOperationException(
                "LongMemEval concurrent preparation did not produce telemetry for every question.");
        return new LongMemEvalPreparedBatchExecution(
            telemetry,
            executionIndexes.Sum(index => (long)plans[index].BatchCount),
            executionIndexes.Sum(index => (long)plans[index].TotalEstimatedInputTokens),
            maximumActive);
    }

    private static string ScopeId(string runId, string kind, int questionNumber) =>
        $"{runId}-{kind}-{questionNumber:D4}";

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(
                ref maximum,
                candidate,
                observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    /// <summary>
    /// The multi-session extractor this executor is built on, or a diagnosable error.
    /// </summary>
    /// <remarks>
    /// Both call sites used <c>Single(extractor =&gt; extractor.IsEnabled)</c>, so running with
    /// <c>UseMultiSessionBatchExtraction=false</c> failed with <c>InvalidOperationException: Sequence
    /// contains no matching element</c> — an opaque message naming neither the option nor the
    /// component, for a configuration a user can legitimately ask for.
    /// <para>
    /// <b>The constraint it now states plainly:</b> prepared-pair preparation IS multi-session batch
    /// execution. It plans batches, seals a manifest over the planned call count and reconciles that
    /// count afterwards, none of which has a single-session equivalent — so the single-session
    /// <c>LlmUnifiedMemoryExtractor</c> cannot be measured through this path. Supporting it means
    /// giving this executor a per-session planning mode with its own accounting, not relaxing a check.
    /// </para>
    /// </remarks>
    private static IMultiSessionUnifiedMemoryExtractor RequirePlanner(IServiceProvider services)
    {
        var planner = services
            .GetServices<IMultiSessionUnifiedMemoryExtractor>()
            .FirstOrDefault(extractor => extractor.IsEnabled);
        if (planner is null)
        {
            throw new InvalidOperationException(
                "Prepared-pair preparation requires an enabled IMultiSessionUnifiedMemoryExtractor, "
                + "which needs both UseUnifiedExtraction and UseMultiSessionBatchExtraction. "
                + "--single-session-unified disables the second, and preparation has no single-session "
                + "planning mode: it plans batches and seals a manifest over the planned call count. "
                + "Measuring the single-session unified extractor requires adding that mode here.");
        }

        return planner;
    }

}

internal sealed record LongMemEvalPreparedBatchExecution(
    IReadOnlyList<LongMemEvalQuestionTelemetry> Telemetry,
    long PlannedCalls,
    long EstimatedInputTokens,
    int MaximumConcurrency);
