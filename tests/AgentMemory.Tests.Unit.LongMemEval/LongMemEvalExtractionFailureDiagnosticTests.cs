using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalExtractionFailureDiagnosticTests
{
    private const string ProtectedFailureText =
        "PROTECTED provider response and request identifier must never escape";

    [Fact]
    public async Task PreparationRejectsProviderFailureAtItsSourceSessionWithoutProtectedText()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = AgentEval.Memory.External.LongMemEval.LongMemEvalHistoryFormatter
            .Format(entry, benchmarkOptions);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        var provider = Substitute.For<IChatClient>();
        var providerCall = 0;
        provider.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                providerCall++;
                if (providerCall == 2)
                    throw new InvalidOperationException(ProtectedFailureText);

                return new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, """{"entities":[]}"""));
            });
        using var meter = new LongMemEvalChatCallMeter(provider);

        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(
                Arg.Any<IEnumerable<Message>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.ExtractAndPersistAsync(
                Arg.Any<ExtractionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var purposes = new[]
                {
                    "You are an entity extraction assistant.",
                    "You are a fact extraction assistant.",
                    "You are a preference extraction assistant.",
                    "You are a relationship extraction assistant."
                };
                foreach (var purpose in purposes)
                {
                    try
                    {
                        _ = await meter.GetResponseAsync(
                            [new ChatMessage(ChatRole.System, purpose)]);
                    }
                    catch (InvalidOperationException)
                    {
                        // Mirrors ExtractorBase<T>: the provider exception becomes an empty
                        // extraction result and the pipeline can still report Succeeded.
                    }
                }

                return new ExtractionResult();
            });

        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            meter,
            "failure-diagnostic-red",
            new LongMemEvalAdapterOptions
            {
                MemoryMode = LongMemEvalMemoryMode.Structured,
                ModelId = "answer-model",
                EvidenceIndex = evidenceIndex,
                EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers,
                PreparationOnly = true,
                RequireGraphReadBack = true,
                GraphProbe = new Probe()
            });
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(history);

        var act = () => adapter.InvokeAsync(
            LongMemEvalEvidenceIndexTests.InvocationPrompt(entry));

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain("question 1");
        failure.Which.Message.Should().Contain("source session 0");
        failure.Which.Message.Should().Contain("4 calls");
        failure.Which.Message.Should().Contain("1 failures");
        failure.Which.Message.Should().Contain("fact");
        failure.Which.Message.Should().Contain(nameof(InvalidOperationException));
        failure.Which.Message.Should().NotContain(ProtectedFailureText);
        adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Status.Should().Be("extraction-provider-accounting-error");
    }

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
