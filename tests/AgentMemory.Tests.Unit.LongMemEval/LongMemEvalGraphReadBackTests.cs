using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using MemoryFact = AgentMemory.Abstractions.Domain.Fact;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalGraphReadBackTests
{
    [Xunit.Fact]
    public async Task StructuredMode_CompleteGraphReadBackPermitsRecallAndEmitsSnapshot()
    {
        var expected = new LongMemEvalGraphSnapshot(
            Entities: 1,
            Facts: 1,
            Preferences: 1,
            Relationships: 1,
            RelationshipsWithProvenance: 1,
            LearnedItems: 3,
            LearnedItemsWithProvenance: 3,
            ProvenanceEdges: 6,
            SourceMessages: 2);
        var harness = CreateHarness(expected);

        await harness.Adapter.ResetSessionAsync();
        harness.Adapter.InjectConversationHistory(harness.History);
        await harness.Adapter.InvokeAsync(harness.Prompt);

        harness.GraphProbe.CallCount.Should().Be(1);
        harness.GraphProbe.OwnerId.Should().NotBeNullOrWhiteSpace();
        await harness.Memory.Received(1).RecallAsync(
            Arg.Any<RecallRequest>(),
            Arg.Any<CancellationToken>());
        var telemetry = harness.Adapter.QuestionTelemetry.Should().ContainSingle().Subject;
        telemetry.Status.Should().Be("completed");
        telemetry.GraphReadBack.Should().Be(expected);
    }

    [Xunit.Fact]
    public async Task StructuredMode_EmptyGraphReadBackFailsBeforeRecall()
    {
        var harness = CreateHarness(new LongMemEvalGraphSnapshot(
            Entities: 0,
            Facts: 0,
            Preferences: 0,
            Relationships: 0,
            RelationshipsWithProvenance: 0,
            LearnedItems: 0,
            LearnedItemsWithProvenance: 0,
            ProvenanceEdges: 0,
            SourceMessages: 0));
        await harness.Adapter.ResetSessionAsync();
        harness.Adapter.InjectConversationHistory(harness.History);

        var act = () => harness.Adapter.InvokeAsync(harness.Prompt);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*did not prove non-empty learned memory with complete provenance*");
        await harness.Memory.DidNotReceive().RecallAsync(
            Arg.Any<RecallRequest>(),
            Arg.Any<CancellationToken>());
        var telemetry = harness.Adapter.QuestionTelemetry.Should().ContainSingle().Subject;
        telemetry.Status.Should().Be("graph-readback-empty");
        telemetry.GraphReadBack.Should().NotBeNull();
        telemetry.GraphReadBack!.TotalLearned.Should().Be(0);
    }

    private static Harness CreateHarness(LongMemEvalGraphSnapshot snapshot)
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = AgentEval.Memory.External.LongMemEval.LongMemEvalHistoryFormatter
            .Format(entry, benchmarkOptions);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.ExtractAndPersistAsync(
                Arg.Any<ExtractionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new RecallResult
            {
                Context = new MemoryContext
                {
                    SessionId = call.Arg<RecallRequest>().SessionId,
                    AssembledAtUtc = DateTimeOffset.UnixEpoch,
                    RelevantFacts = new MemoryContextSection<MemoryFact>
                    {
                        Items =
                        [
                            new MemoryFact
                            {
                                FactId = "fact-1",
                                Subject = "user",
                                Predicate = "visited",
                                Object = "Japan",
                                Confidence = 0.9,
                                CreatedAtUtc = DateTimeOffset.UnixEpoch
                            }
                        ]
                    }
                },
                TotalItemsRetrieved = 1
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "The user visited Japan.")));
        var graphProbe = new FakeGraphProbe(snapshot);
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            chat,
            "graph-readback-run",
            new LongMemEvalAdapterOptions
            {
                MemoryMode = LongMemEvalMemoryMode.Structured,
                EvidenceIndex = evidenceIndex,
                EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers,
                RequireGraphReadBack = true,
                GraphProbe = graphProbe
            });
        return new Harness(
            adapter,
            memory,
            graphProbe,
            history,
            LongMemEvalEvidenceIndexTests.InvocationPrompt(entry));
    }

    private sealed record Harness(
        AgentMemoryLongMemEvalAdapter Adapter,
        IMemoryService Memory,
        FakeGraphProbe GraphProbe,
        IReadOnlyList<(string UserMessage, string AssistantResponse)> History,
        string Prompt);

    private sealed class FakeGraphProbe(LongMemEvalGraphSnapshot snapshot)
        : ILongMemEvalGraphProbe
    {
        public int CallCount { get; private set; }

        public string? OwnerId { get; private set; }

        public Task<LongMemEvalGraphSnapshot> ReadAsync(
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            OwnerId = ownerId;
            return Task.FromResult(snapshot);
        }
    }
}
