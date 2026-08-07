using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalPreparedAdapterFailureTests
{
    [Fact]
    public async Task ManifestHistoryMismatchFailsBeforeAnyMemoryCall()
    {
        var fixture = Fixture(
            historySha256: "deliberately-wrong-history-fingerprint",
            probeSnapshot: Snapshot());

        var act = () => fixture.Adapter.InvokeAsync(fixture.Prompt);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*sealed manifest*");
        fixture.Adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Status.Should().Be("prepared-manifest-mismatch");
        await AssertNoMemoryCalls(fixture.Memory);
    }

    [Fact]
    public async Task GraphMutationFailsBeforeRecall()
    {
        var fixture = Fixture(
            historySha256: null,
            probeSnapshot: Snapshot() with { Facts = 2 });

        var act = () => fixture.Adapter.InvokeAsync(fixture.Prompt);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*graph state*sealed snapshot*");
        fixture.Adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Status.Should().Be("prepared-graph-mismatch");
        await AssertNoMemoryCalls(fixture.Memory);
    }

    private static PreparedFixture Fixture(
        string? historySha256,
        LongMemEvalGraphSnapshot probeSnapshot)
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = AgentEval.Memory.External.LongMemEval.LongMemEvalHistoryFormatter
            .Format(entry, benchmarkOptions);
        var prompt = LongMemEvalEvidenceIndexTests.InvocationPrompt(entry);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        var evidenceQuestion = evidenceIndex.GetByQuestionId(entry.QuestionId);
        var sourceSessions = evidenceQuestion.Messages
            .Where(message =>
                !message.IsSyntheticBoundary &&
                !message.IsSyntheticFormatterPadding)
            .Select(message => message.SourceSessionOrdinal)
            .Distinct()
            .Count();
        var manifest = LongMemEvalPreparationManifest.Create(
            "prepared-failure-test",
            "dataset-sha256",
            "agenteval-revision",
            "prepared-run",
            "answer-model",
            "judge-model",
            "extraction-model",
            "embedding-model",
            1536,
            30,
            "metadata-only-not-in-extraction-prompt",
            [
                new LongMemEvalPreparedQuestion(
                    1,
                    evidenceQuestion.QuestionId,
                    historySha256 ?? LongMemEvalEvidenceIndex.Fingerprint(history),
                    LongMemEvalPreparationManifest.Hash(
                        "prepared-run-session-0001|prepared-run-owner-0001"),
                    evidenceQuestion.Messages.Count(m =>
                        !m.IsSyntheticBoundary && !m.IsSyntheticFormatterPadding),
                    sourceSessions,
                    sourceSessions,
                    Snapshot())
            ],
            sourceSessions * 4);
        var memory = Substitute.For<IMemoryService>();
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            Substitute.For<IChatClient>(),
            "prepared-run",
            new LongMemEvalAdapterOptions
            {
                MemoryMode = LongMemEvalMemoryMode.Structured,
                PreparedMemory = true,
                PreparedState = new LongMemEvalPreparedState(manifest, "prepared-run"),
                MaxRelevantMessages = 30,
                ModelId = "answer-model",
                EvidenceIndex = evidenceIndex,
                EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers,
                RequireGraphReadBack = true,
                GraphProbe = new Probe(probeSnapshot)
            });
        adapter.ResetSessionAsync().GetAwaiter().GetResult();
        adapter.InjectConversationHistory(history);
        return new PreparedFixture(adapter, memory, prompt);
    }

    private static async Task AssertNoMemoryCalls(IMemoryService memory)
    {
        await memory.DidNotReceive().AddMessagesAsync(
            Arg.Any<IEnumerable<Message>>(),
            Arg.Any<CancellationToken>());
        await memory.DidNotReceive().ExtractAndPersistAsync(
            Arg.Any<ExtractionRequest>(),
            Arg.Any<CancellationToken>());
        await memory.DidNotReceive().RecallAsync(
            Arg.Any<RecallRequest>(),
            Arg.Any<CancellationToken>());
    }

    private static LongMemEvalGraphSnapshot Snapshot() =>
        new(1, 1, 1, 1, 1, 3, 3, 6, 2);

    private sealed class Probe(LongMemEvalGraphSnapshot snapshot) : ILongMemEvalGraphProbe
    {
        public Task<LongMemEvalGraphSnapshot> ReadAsync(
            string ownerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed record PreparedFixture(
        AgentMemoryLongMemEvalAdapter Adapter,
        IMemoryService Memory,
        string Prompt);
}
