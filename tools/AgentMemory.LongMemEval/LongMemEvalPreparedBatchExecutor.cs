using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.LongMemEval;

internal static class LongMemEvalPreparedBatchExecutor
{
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

        var planner = services
            .GetServices<IMultiSessionUnifiedMemoryExtractor>()
            .Single(extractor => extractor.IsEnabled);
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

        var telemetry = new LongMemEvalQuestionTelemetry[questions.Count];
        var active = 0;
        var maximumActive = 0;
        var completed = 0;
        var driver = services.GetRequiredService<IDriver>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, questions.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = preparationWorkers,
                CancellationToken = cancellationToken
            },
            async (index, itemCancellationToken) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                try
                {
                    await using var scope = services.CreateAsyncScope();
                    var scoped = scope.ServiceProvider;
                    var planner = scoped
                        .GetServices<IMultiSessionUnifiedMemoryExtractor>()
                        .Single(extractor => extractor.IsEnabled);
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

                    telemetry[index] = questionTelemetry[0];
                    var completedNow = Interlocked.Increment(ref completed);
                    Console.WriteLine(
                        $"longmemeval: prepared question {completedNow}/{questions.Count} " +
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
            plans.Sum(plan => (long)plan.BatchCount),
            plans.Sum(plan => (long)plan.TotalEstimatedInputTokens),
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
}

internal sealed record LongMemEvalPreparedBatchExecution(
    IReadOnlyList<LongMemEvalQuestionTelemetry> Telemetry,
    long PlannedCalls,
    long EstimatedInputTokens,
    int MaximumConcurrency);
