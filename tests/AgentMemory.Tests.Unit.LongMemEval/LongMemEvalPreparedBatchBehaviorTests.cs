using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalPreparedBatchBehaviorTests
{
    [Fact]
    public void CheckpointSelection_PicksHighestTokenQuestionsWithStableTies()
    {
        var plans = new[]
        {
            Plan(100),
            Plan(300),
            Plan(300),
            Plan(200)
        };

        var selected = LongMemEvalPreparedBatchExecutor
            .SelectCheckpointQuestionIndexes(plans, 2);

        selected.Should().Equal(1, 2);
    }

    [Fact]
    public void CheckpointProjection_UsesWorstScaleAndSafetyMargin()
    {
        var projected = LongMemEvalPreparedBatchExecutor
            .ProjectFullPreparationMilliseconds(
                fullCalls: 12,
                fullSourceSessions: 48,
                fullEstimatedInputTokens: 1_200,
                checkpointCalls: 3,
                checkpointSourceSessions: 12,
                checkpointEstimatedInputTokens: 300,
                checkpointWallMilliseconds: 10_000,
                profileStartupMilliseconds: 2_000);

        projected.Should().Be(52_000);
    }

    private static MultiSessionExtractionPlan Plan(int tokens) =>
        new(
        [
            new MultiSessionExtractionBatchPlan([Guid.NewGuid().ToString("N")], tokens)
        ]);

    [Fact]
    public async Task BatchedPreparation_UsesOneUnifiedCallForThreeSourceSessions()
    {
        var harness = CreateHarness(extraProviderCalls: 0);

        await harness.Adapter.ResetSessionAsync();
        harness.Adapter.InjectConversationHistory(harness.History);
        await harness.Adapter.InvokeAsync(harness.Question.InvocationPrompt);

        harness.Pipeline.BatchInvocations.Should().Be(1);
        harness.Pipeline.LastRequests.Should().HaveCount(3);
        await harness.Memory.DidNotReceive().ExtractAndPersistAsync(
            Arg.Any<ExtractionRequest>(),
            Arg.Any<CancellationToken>());
        harness.Meter.Snapshot().Calls.Should().Be(1);
        harness.Adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Should().Match<LongMemEvalQuestionTelemetry>(item =>
                item.Status == "prepared" &&
                item.ExtractionUnits == 3 &&
                item.ExtractionCallsPlanned == 1 &&
                item.GraphReadBack != null &&
                item.GraphReadBack.CompleteProvenance);
    }

    [Fact]
    public async Task BatchedPreparation_FailsClosedOnAnUnplannedProviderCall()
    {
        var harness = CreateHarness(extraProviderCalls: 1);
        await harness.Adapter.ResetSessionAsync();
        harness.Adapter.InjectConversationHistory(harness.History);

        var act = () => harness.Adapter.InvokeAsync(harness.Question.InvocationPrompt);

        await act.Should().ThrowAsync<LongMemEvalExtractionAccountingException>()
            .WithMessage("*observed 2 calls*expected exactly 1*");
        harness.Adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Status.Should().Be("extraction-provider-accounting-error");
    }
    [Fact]
    public async Task BatchedPreparation_RejectsMissingSessionAcknowledgement()
    {
        var harness = CreateHarness(extraProviderCalls: 0);
        harness.Pipeline.DropLastResult = true;
        await harness.Adapter.ResetSessionAsync();
        harness.Adapter.InjectConversationHistory(harness.History);

        var act = () => harness.Adapter.InvokeAsync(harness.Question.InvocationPrompt);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*did not persist every planned source session*");
    }

    [Fact]
    public async Task BatchedPreparation_RejectsOwnerOrderDeviation()
    {
        var harness = CreateHarness(extraProviderCalls: 0);
        harness.Pipeline.ReverseResults = true;
        await harness.Adapter.ResetSessionAsync();
        harness.Adapter.InjectConversationHistory(harness.History);

        var act = () => harness.Adapter.InvokeAsync(harness.Question.InvocationPrompt);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*did not persist every planned source session*");
    }

    [Fact]
    public async Task BatchedPreparation_PropagatesProviderFailureAndRecordsIt()
    {
        var harness = CreateHarness(extraProviderCalls: 0, providerFailure: true);
        await harness.Adapter.ResetSessionAsync();
        harness.Adapter.InjectConversationHistory(harness.History);

        var act = () => harness.Adapter.InvokeAsync(harness.Question.InvocationPrompt);

        await act.Should().ThrowAsync<Exception>();
        harness.Meter.Snapshot().Failures.Should().Be(1);
        harness.Adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Status.Should().Be("extraction-error");
    }


    [Fact]
    public async Task ScopedMeter_SeparatesConcurrentUnifiedBatchQuestions()
    {
        var provider = SuccessfulProvider();
        using var meter = new LongMemEvalChatCallMeter(provider);

        await Task.WhenAll(Enumerable.Range(1, 4).Select(async question =>
        {
            using (meter.BeginScope($"question-{question}"))
            {
                await UnifiedBatchCallAsync(meter);
            }
        }));

        meter.Snapshot().Calls.Should().Be(4);
        foreach (var question in Enumerable.Range(1, 4))
        {
            var scope = meter.SnapshotScope($"question-{question}");
            scope.Calls.Should().Be(1);
            scope.Failures.Should().Be(0);
            scope.Purposes.Should().ContainSingle()
                .Which.Should().Be(new KeyValuePair<string, long>("unified_batch", 1));
        }
    }

    private static Harness CreateHarness(
        int extraProviderCalls, bool providerFailure = false)
    {
        const string runId = "batched-preparation";
        var entry = ThreeSessionEntry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = LongMemEvalHistoryFormatter.Format(entry, benchmarkOptions);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        var question = evidenceIndex.Questions.Single();
        var provider = SuccessfulProvider(providerFailure);
        var meter = new LongMemEvalChatCallMeter(provider);
        var planner = new DeterministicPlanner();
        var pipeline = new RecordingBatchPipeline(meter, planner, extraProviderCalls);
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(
                Arg.Any<IEnumerable<Message>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.ExtractAndPersistAsync(
                Arg.Any<ExtractionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ExtractionResult>(
                new InvalidOperationException(
                    "Legacy extraction must not run.")));

        var messages = AgentMemoryLongMemEvalAdapter.BuildMessages(
            runId,
            history,
            runId + "-session-0001",
            runId + "-owner-0001",
            1,
            question,
            new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal));
        var requests = AgentMemoryLongMemEvalAdapter.BuildExtractionRequests(
            messages,
            question,
            runId + "-session-0001",
            runId + "-owner-0001");
        var plan = planner.Plan(requests, 4, 100_000);
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            meter,
            runId,
            new LongMemEvalAdapterOptions
            {
                MemoryMode = LongMemEvalMemoryMode.Structured,
                ModelId = "extraction-model",
                EvidenceIndex = evidenceIndex,
                EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers,
                PreparationOnly = true,
                RequireGraphReadBack = true,
                GraphProbe = new CompleteGraphProbe(),
                UseBatchedPreparation = true,
                BatchExtractionPipeline = pipeline,
                BatchPlanner = planner,
                MaxSessionsPerBatch = 4,
                MaxInputTokens = 100_000,
                ExpectedExtractionPlan = plan
            });
        return new Harness(adapter, memory, meter, pipeline, history, question);
    }

    private static IChatClient SuccessfulProvider(bool fail = false)
    {
        var provider = Substitute.For<IChatClient>();
        var call = provider.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
        if (fail)
        {
            call.Returns(Task.FromException<ChatResponse>(
                new HttpRequestException("provider failure")));
        }
        else
        {
            call.Returns(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, """{"sessions":[]}""")));
        }
        return provider;
    }

    private static Task<ChatResponse> UnifiedBatchCallAsync(IChatClient client) =>
        client.GetResponseAsync(
        [
            new ChatMessage(
                ChatRole.System,
                "You extract structured long-term memory from multiple independent source sessions. content-free test")
        ]);

    private static LongMemEvalEntry ThreeSessionEntry()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        entry.HaystackSessionIds = ["session-1", "session-2", "session-3"];
        entry.HaystackDates =
        [
            "2024/01/01 (Mon) 10:00",
            "2024/01/02 (Tue) 10:00",
            "2024/01/03 (Wed) 10:00"
        ];
        entry.AnswerSessionIds = ["session-2"];
        entry.HaystackSessions =
        [
            Session("session one"),
            Session("session two"),
            Session("session three")
        ];
        return entry;
    }

    private static List<LongMemEvalTurn> Session(string label) =>
    [
        new LongMemEvalTurn
        {
            Role = "user",
            Content = label + " user message",
            HasAnswer = label == "session two"
        },
        new LongMemEvalTurn
        {
            Role = "assistant",
            Content = label + " assistant message",
            HasAnswer = false
        }
    ];

    private sealed class DeterministicPlanner : IMultiSessionUnifiedMemoryExtractor
    {
        public bool IsEnabled => true;

        public MultiSessionExtractionPlan Plan(
            IReadOnlyList<ExtractionRequest> requests,
            int maxSessionsPerBatch,
            int maxInputTokens) =>
            new(
            [
                new MultiSessionExtractionBatchPlan(
                    requests.Select(request => request.SessionId).ToArray(),
                    500)
            ]);

        public Task<IReadOnlyDictionary<string, UnifiedExtractionResult>> ExtractAsync(
            IReadOnlyList<ExtractionRequest> requests,
            int maxSessionsPerBatch,
            int maxInputTokens,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingBatchPipeline(
        LongMemEvalChatCallMeter meter,
        DeterministicPlanner planner,
        int extraProviderCalls) : IMemoryExtractionPipeline
    {
        public int BatchInvocations { get; private set; }
        public IReadOnlyList<ExtractionRequest> LastRequests { get; private set; } = [];
        public bool DropLastResult { get; set; }
        public bool ReverseResults { get; set; }

        public Task<ExtractionResult> ExtractAsync(
            ExtractionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Legacy extraction must not run.");

        public async Task<IReadOnlyList<ExtractionResult>> ExtractBatchAsync(
            IReadOnlyList<ExtractionRequest> requests,
            int maxSessionsPerBatch,
            int maxInputTokens,
            CancellationToken cancellationToken = default)
        {
            BatchInvocations++;
            LastRequests = requests;
            var plan = planner.Plan(requests, maxSessionsPerBatch, maxInputTokens);
            for (var call = 0; call < plan.BatchCount + extraProviderCalls; call++)
                await UnifiedBatchCallAsync(meter);
            var results = plan.Batches
                .SelectMany(batch => batch.SourceSessionIds)
                .Select(sessionId => new ExtractionResult
                {
                    Status = IngestionStatus.Succeeded,
                    Metadata = new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId
                    }
                })
                .ToArray();
            if (DropLastResult)
                results = results.Take(results.Length - 1).ToArray();
            if (ReverseResults)
                results = results.AsEnumerable().Reverse().ToArray();
            return results;
        }
    }

    private sealed class CompleteGraphProbe : ILongMemEvalGraphProbe
    {
        public Task<LongMemEvalGraphSnapshot> ReadAsync(
            string ownerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LongMemEvalGraphSnapshot(
                Entities: 1,
                Facts: 1,
                Preferences: 1,
                Relationships: 1,
                RelationshipsWithProvenance: 1,
                LearnedItems: 4,
                LearnedItemsWithProvenance: 4,
                ProvenanceEdges: 4,
                SourceMessages: 6));
    }

    private sealed record Harness(
        AgentMemoryLongMemEvalAdapter Adapter,
        IMemoryService Memory,
        LongMemEvalChatCallMeter Meter,
        RecordingBatchPipeline Pipeline,
        IReadOnlyList<(string UserMessage, string AssistantResponse)> History,
        LongMemEvalEvidenceQuestion Question);
}
