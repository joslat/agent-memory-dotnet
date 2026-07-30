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

public sealed class LongMemEvalExtraCallDiagnosticTests
{
    [Fact]
    public async Task PreparationExtraCallsIdentifyTheRepeatedSuccessfulPurpose()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = LongMemEvalHistoryFormatter.Format(entry, benchmarkOptions);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        var provider = SuccessfulProvider();
        using var meter = new LongMemEvalChatCallMeter(provider);
        var memory = MemoryWithExtraction(async () =>
        {
            await CallAsync(meter, "entity");
            await CallAsync(meter, "fact");
            await CallAsync(meter, "preference");
            await CallAsync(meter, "relationship");
            await CallAsync(meter, "relationship");
            await CallAsync(meter, "relationship");
        });
        var adapter = Adapter(memory, meter, evidenceIndex);
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(history);

        var act = () => adapter.InvokeAsync(
            LongMemEvalEvidenceIndexTests.InvocationPrompt(entry));

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain(
            "Call purposes: entity=1, fact=1, preference=1, relationship=3.");
        failure.Which.Message.Should().NotContain("sensitive");
    }

    [Fact]
    public async Task DiagnosticSourceSessionSelectorRunsExactlyOneUnit()
    {
        var entry = ThreeSessionEntry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = LongMemEvalHistoryFormatter.Format(entry, benchmarkOptions);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        using var meter = new LongMemEvalChatCallMeter(SuccessfulProvider());
        ExtractionRequest? request = null;
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(
                Arg.Any<IEnumerable<Message>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.ExtractAndPersistAsync(
                Arg.Any<ExtractionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                request = call.Arg<ExtractionRequest>();
                await CallAsync(meter, "entity");
                await CallAsync(meter, "fact");
                await CallAsync(meter, "preference");
                await CallAsync(meter, "relationship");
                return new ExtractionResult();
            });
        var progress = new List<(int Completed, int Total)>();
        var options = Options(evidenceIndex) with
        {
            ExtractionProgress = (completed, total) =>
                progress.Add((completed, total))
        };
        var selector = typeof(LongMemEvalAdapterOptions)
            .GetProperty(
                "DiagnosticSourceSessionOrdinal",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
        selector.Should().NotBeNull(
            "the locked diagnostic must select one source-session unit without changing benchmark acceptance");
        selector!.SetValue(options, 1);
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory, meter, "single-unit-red", options);
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(history);

        await adapter.InvokeAsync(LongMemEvalEvidenceIndexTests.InvocationPrompt(entry));

        await memory.Received(1).ExtractAndPersistAsync(
            Arg.Any<ExtractionRequest>(),
            Arg.Any<CancellationToken>());
        request.Should().NotBeNull();
        request!.SessionId.Should().EndWith("-source-0001");
        request.Messages.Should().HaveCount(2);
        request.Messages.Should().OnlyContain(message =>
            message.Content.Contains("session two", StringComparison.Ordinal));
        progress.Should().Equal((0, 1), (1, 1));
        adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.ExtractionUnits.Should().Be(1);
    }

    private static AgentMemoryLongMemEvalAdapter Adapter(
        IMemoryService memory,
        LongMemEvalChatCallMeter meter,
        LongMemEvalEvidenceIndex evidenceIndex) =>
        new(memory, meter, "extra-call-red", Options(evidenceIndex));

    private static LongMemEvalAdapterOptions Options(
        LongMemEvalEvidenceIndex evidenceIndex) =>
        new()
        {
            MemoryMode = LongMemEvalMemoryMode.Structured,
            ModelId = "answer-model",
            EvidenceIndex = evidenceIndex,
            EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers,
            PreparationOnly = true,
            RequireGraphReadBack = true,
            GraphProbe = new Probe()
        };

    private static IMemoryService MemoryWithExtraction(Func<Task> extraction)
    {
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(
                Arg.Any<IEnumerable<Message>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.ExtractAndPersistAsync(
                Arg.Any<ExtractionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await extraction();
                return new ExtractionResult();
            });
        return memory;
    }

    private static IChatClient SuccessfulProvider()
    {
        var provider = Substitute.For<IChatClient>();
        provider.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, """{"entities":[]}""")));
        return provider;
    }

    private static Task<ChatResponse> CallAsync(
        IChatClient client,
        string purpose) =>
        client.GetResponseAsync(
        [
            new ChatMessage(
                ChatRole.System,
                $"You are {(purpose == "entity" ? "an" : "a")} {purpose} extraction assistant. sensitive prompt")
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
            Content = $"{label} user message",
            HasAnswer = label == "session two"
        },
        new LongMemEvalTurn
        {
            Role = "assistant",
            Content = $"{label} assistant message",
            HasAnswer = false
        }
    ];

    private sealed class Probe : ILongMemEvalGraphProbe
    {
        public Task<LongMemEvalGraphSnapshot> ReadAsync(
            string ownerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LongMemEvalGraphSnapshot(
                Entities: 1,
                Facts: 0,
                Preferences: 0,
                Relationships: 0,
                RelationshipsWithProvenance: 0,
                LearnedItems: 1,
                LearnedItemsWithProvenance: 1,
                ProvenanceEdges: 1,
                SourceMessages: 1));
    }
}
