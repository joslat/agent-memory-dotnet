using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

/// <summary>
/// Unit tests for retroactive extraction and batch re-embedding methods on MemoryService.
/// </summary>
public sealed class MemoryServiceBatchTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly IMemoryContextAssembler _assembler = Substitute.For<IMemoryContextAssembler>();
    private readonly IMemoryExtractionPipeline _extraction = Substitute.For<IMemoryExtractionPipeline>();
    private readonly IEntityRepository _entityRepo = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _factRepo = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _prefRepo = Substitute.For<IPreferenceRepository>();
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();

    private static readonly DateTimeOffset FixedTime = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public MemoryServiceBatchTests()
    {
        _clock.UtcNow.Returns(FixedTime);
        _idGenerator.GenerateId().Returns("gen-id");
        _extraction.ExtractAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult());
    }

    private MemoryService CreateSut() =>
        new(_shortTerm, _assembler, _extraction,
            _entityRepo, _factRepo, _prefRepo, _embeddingOrchestrator,
            Options.Create(new MemoryOptions()),
            _clock, _idGenerator,
            NullLogger<MemoryService>.Instance);

    private static Message MakeMessage(string id, string sessionId, string convId = "conv-1") => new()
    {
        MessageId = id,
        SessionId = sessionId,
        ConversationId = convId,
        Role = "user",
        Content = "test",
        TimestampUtc = FixedTime
    };

    // ── ExtractFromSessionAsync ──

    [Fact]
    public async Task ExtractFromSessionAsync_GetsMessagesAndRunsExtraction()
    {
        var messages = new List<Message> { MakeMessage("m1", "sess-1"), MakeMessage("m2", "sess-1") };
        _shortTerm.GetRecentMessagesAsync("sess-1", int.MaxValue, Arg.Any<CancellationToken>())
            .Returns(messages);

        var sut = CreateSut();
        await sut.ExtractFromSessionAsync("sess-1");

        await _extraction.Received(1).ExtractAsync(
            Arg.Is<ExtractionRequest>(r =>
                r.SessionId == "sess-1" &&
                r.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractFromSessionAsync_WhenNoMessages_SkipsExtraction()
    {
        _shortTerm.GetRecentMessagesAsync("empty-sess", int.MaxValue, Arg.Any<CancellationToken>())
            .Returns(new List<Message>());

        var sut = CreateSut();
        await sut.ExtractFromSessionAsync("empty-sess");

        await _extraction.DidNotReceive().ExtractAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
    }

    // ── ExtractFromConversationAsync ──

    [Fact]
    public async Task ExtractFromConversationAsync_GetsConversationMessagesAndRunsExtraction()
    {
        var messages = new List<Message>
        {
            MakeMessage("m1", "sess-1", "conv-10"),
            MakeMessage("m2", "sess-1", "conv-10")
        };
        _shortTerm.GetConversationMessagesAsync("conv-10", Arg.Any<CancellationToken>())
            .Returns(messages);

        var sut = CreateSut();
        await sut.ExtractFromConversationAsync("conv-10");

        await _extraction.Received(1).ExtractAsync(
            Arg.Is<ExtractionRequest>(r =>
                r.SessionId == "sess-1" &&
                r.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractFromConversationAsync_WhenNoMessages_SkipsExtraction()
    {
        _shortTerm.GetConversationMessagesAsync("empty-conv", Arg.Any<CancellationToken>())
            .Returns(new List<Message>());

        var sut = CreateSut();
        await sut.ExtractFromConversationAsync("empty-conv");

        await _extraction.DidNotReceive().ExtractAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
    }

    // ── GenerateEmbeddingsBatchAsync — Entity ──

    [Fact]
    public async Task GenerateEmbeddingsBatchAsync_Entity_FindsNullEmbeddingEntitiesAndGenerates()
    {
        var entities = new List<Entity>
        {
            new() { EntityId = "e1", Name = "London", Type = "LOCATION", Confidence = 1.0, CreatedAtUtc = FixedTime },
            new() { EntityId = "e2", Name = "Paris",  Type = "LOCATION", Confidence = 1.0, CreatedAtUtc = FixedTime }
        };
        // First page has items with hasNextPage=false; loop should not call again
        _entityRepo.GetPageWithoutEmbeddingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Entity>(entities, hasNextPage: false));
        _embeddingOrchestrator.EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 0.1f }));

        var sut = CreateSut();
        var count = await sut.GenerateEmbeddingsBatchAsync("Entity", batchSize: 100);

        count.Should().Be(2);
        await _embeddingOrchestrator.Received(2).EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _entityRepo.Received(1).UpdateEmbeddingAsync("e1", Arg.Any<float[]>(), Arg.Any<CancellationToken>());
        await _entityRepo.Received(1).UpdateEmbeddingAsync("e2", Arg.Any<float[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateEmbeddingsBatchAsync_Fact_UsesSpoTripleAsEmbeddingText()
    {
        var fact = new Fact
        {
            FactId = "f1", Subject = "Alice", Predicate = "works_at", Object = "Acme",
            Confidence = 1.0, CreatedAtUtc = FixedTime
        };
        _factRepo.GetPageWithoutEmbeddingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Fact>(new List<Fact> { fact }, hasNextPage: false));
        _embeddingOrchestrator.EmbedFactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 0.5f }));

        var sut = CreateSut();
        await sut.GenerateEmbeddingsBatchAsync("Fact", batchSize: 100);

        await _embeddingOrchestrator.Received(1)
            .EmbedFactAsync("Alice", "works_at", "Acme", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateEmbeddingsBatchAsync_Preference_UsesPreferenceText()
    {
        var pref = new Preference
        {
            PreferenceId = "p1", Category = "style", PreferenceText = "Prefers dark mode",
            Confidence = 1.0, CreatedAtUtc = FixedTime
        };
        _prefRepo.GetPageWithoutEmbeddingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Preference>(new List<Preference> { pref }, hasNextPage: false));
        _embeddingOrchestrator.EmbedPreferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 0.3f }));

        var sut = CreateSut();
        await sut.GenerateEmbeddingsBatchAsync("Preference", batchSize: 100);

        await _embeddingOrchestrator.Received(1)
            .EmbedPreferenceAsync("Prefers dark mode", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateEmbeddingsBatchAsync_UnsupportedLabel_ThrowsArgumentException()
    {
        var sut = CreateSut();
        var act = () => sut.GenerateEmbeddingsBatchAsync("Message");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Message*");
    }
}
