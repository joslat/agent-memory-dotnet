using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// BUG-M1. The batch path guarded <c>EmbedBatchAsync</c> by count alone, so a provider returning the
/// right NUMBER of empty vectors had them persisted verbatim. An empty list is not null, so such a
/// message survives the recall filter <c>node.embedding IS NOT NULL</c>, then cosine yields null and
/// <c>null &gt;= $minScore</c> is false — every row is dropped. The message is stored and permanently
/// unretrievable, with no error anywhere. That is the LongMemEval "596 stored / 0 recalled" signature.
/// </summary>
public sealed class ShortTermMemoryEmbeddingValidationTests
{
    private readonly IMessageRepository _messageRepo = Substitute.For<IMessageRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();

    private ShortTermMemoryService CreateSut()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("n"));
        _messageRepo.AddBatchAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<IReadOnlyList<Message>>()));

        return new ShortTermMemoryService(
            Substitute.For<IConversationRepository>(),
            _messageRepo,
            Substitute.For<IReasoningTraceRepository>(),
            _embeddings,
            clock,
            ids,
            Options.Create(new ShortTermMemoryOptions()),
            NullLogger<ShortTermMemoryService>.Instance);
    }

    private static Message[] Messages(int count) =>
        Enumerable.Range(0, count).Select(index => new Message
        {
            MessageId = $"m-{index}",
            SessionId = "s-1",
            ConversationId = "c-1",
            Role = "user",
            Content = $"message {index}",
            TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(index)
        }).ToArray();

    /// <summary>The exact defect: correct count, unusable contents, silently persisted.</summary>
    [Fact]
    public async Task AddMessagesAsync_BatchReturnsCorrectlyCountedEmptyVectors_DoesNotPersistUnsearchableMessages()
    {
        var sut = CreateSut();
        var messages = Messages(3);
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>([[], [], []]));
        _embeddings.EmbedMessageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<float>()));

        var act = async () => await sut.AddMessagesAsync(messages);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "persisting a message that can never be retrieved is worse than failing the write");
        await _messageRepo.DidNotReceive().AddBatchAsync(
            Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>One bad slot must be replayed individually, not fail the whole batch.</summary>
    [Fact]
    public async Task AddMessagesAsync_SingleEmptySlot_ReplaysThatSlotAndPersists()
    {
        var sut = CreateSut();
        var messages = Messages(3);
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>([[1f, 0f], [], [0f, 1f]]));
        _embeddings.EmbedMessageAsync("message 1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[] { 0.5f, 0.5f }));

        var stored = await sut.AddMessagesAsync(messages);

        stored.Should().HaveCount(3);
        stored.Should().OnlyContain(m => m.Embedding != null && m.Embedding.Length > 0,
            "every persisted message must carry a usable vector");
        stored[1].Embedding.Should().Equal([0.5f, 0.5f]);
    }

    /// <summary>
    /// Blank content has no embedding by definition — the orchestrator returns an empty vector for it
    /// on purpose. That is a property of the input, not a provider failure, so it must not fail the
    /// write; it simply never matches a semantic search.
    /// </summary>
    [Fact]
    public async Task AddMessagesAsync_BlankContent_IsStoredWithoutFailingTheWrite()
    {
        var sut = CreateSut();
        var messages = Messages(2);
        messages[1] = messages[1] with { Content = "   " };
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>([[1f, 0f], []]));

        var stored = await sut.AddMessagesAsync(messages);

        stored.Should().HaveCount(2);
        await _embeddings.DidNotReceive().EmbedMessageAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A healthy batch must be untouched — no extra provider calls, no behaviour change.</summary>
    [Fact]
    public async Task AddMessagesAsync_HealthyBatch_PersistsUnchangedWithNoIndividualReplay()
    {
        var sut = CreateSut();
        var messages = Messages(2);
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>([[1f, 0f], [0f, 1f]]));

        var stored = await sut.AddMessagesAsync(messages);

        stored.Should().HaveCount(2);
        stored[0].Embedding.Should().Equal(1f, 0f);
        stored[1].Embedding.Should().Equal(0f, 1f);
        await _embeddings.DidNotReceive().EmbedMessageAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
