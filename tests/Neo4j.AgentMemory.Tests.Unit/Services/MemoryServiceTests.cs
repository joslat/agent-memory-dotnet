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

public sealed class MemoryServiceTests
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly IMemoryContextAssembler _assembler;
    private readonly IMemoryExtractionPipeline _extractionPipeline;
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly DateTimeOffset _fixedTime = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public MemoryServiceTests()
    {
        _shortTerm = Substitute.For<IShortTermMemoryService>();
        _assembler = Substitute.For<IMemoryContextAssembler>();
        _extractionPipeline = Substitute.For<IMemoryExtractionPipeline>();
        _entityRepository = Substitute.For<IEntityRepository>();
        _factRepository = Substitute.For<IFactRepository>();
        _preferenceRepository = Substitute.For<IPreferenceRepository>();
        _embeddingProvider = Substitute.For<IEmbeddingProvider>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _clock.UtcNow.Returns(_fixedTime);
        _idGenerator.GenerateId().Returns("generated-msg-id");

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateEmptyContext("session-1")));

        _shortTerm
            .AddMessageAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Message>()));

        _shortTerm
            .AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Message>>(ci.Arg<IEnumerable<Message>>().ToList()));

        _shortTerm
            .ClearSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _extractionPipeline
            .ExtractAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExtractionResult()));
    }

    private MemoryService CreateSut(IOptions<MemoryOptions>? options = null) =>
        new(_shortTerm, _assembler, _extractionPipeline,
            _entityRepository, _factRepository, _preferenceRepository, _embeddingProvider,
            options ?? Options.Create(new MemoryOptions()),
            _clock, _idGenerator,
            NullLogger<MemoryService>.Instance);

    [Fact]
    public async Task RecallAsync_AssemblesContextAndWrapsInResult()
    {
        var sut = CreateSut();
        var request = new RecallRequest
        {
            SessionId = "session-1",
            Query = "What do I know?"
        };

        var result = await sut.RecallAsync(request);

        result.Should().NotBeNull();
        result.Context.Should().NotBeNull();
        result.Context.SessionId.Should().Be("session-1");
        await _assembler.Received(1).AssembleContextAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMessageAsync_CreatesMessageAndAddsThroughShortTerm()
    {
        var sut = CreateSut();

        var result = await sut.AddMessageAsync("session-1", "conv-1", "user", "Hello world");

        result.Should().NotBeNull();
        result.MessageId.Should().Be("generated-msg-id");
        result.SessionId.Should().Be("session-1");
        result.ConversationId.Should().Be("conv-1");
        result.Role.Should().Be("user");
        result.Content.Should().Be("Hello world");
        result.TimestampUtc.Should().Be(_fixedTime);

        await _shortTerm
            .Received(1)
            .AddMessageAsync(
                Arg.Is<Message>(m =>
                    m.SessionId == "session-1" &&
                    m.ConversationId == "conv-1" &&
                    m.Role == "user" &&
                    m.Content == "Hello world"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMessagesAsync_DelegatesToShortTerm()
    {
        var sut = CreateSut();
        var messages = new[]
        {
            CreateMessage("msg-1", "session-1"),
            CreateMessage("msg-2", "session-1")
        };

        var result = await sut.AddMessagesAsync(messages);

        result.Should().HaveCount(2);
        await _shortTerm
            .Received(1)
            .AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAndPersistAsync_DelegatesToPipeline()
    {
        var sut = CreateSut();
        var request = new ExtractionRequest
        {
            SessionId = "session-1",
            Messages = new[] { CreateMessage("msg-1", "session-1") }
        };

        var result = await sut.ExtractAndPersistAsync(request);

        result.Should().NotBeNull();
        await _extractionPipeline
            .Received(1)
            .ExtractAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearSessionAsync_DelegatesToShortTerm()
    {
        var sut = CreateSut();

        await sut.ClearSessionAsync("session-1");

        await _shortTerm.Received(1).ClearSessionAsync("session-1", Arg.Any<CancellationToken>());
    }

    // ---- Helpers ----

    private static MemoryContext CreateEmptyContext(string sessionId) => new()
    {
        SessionId = sessionId,
        AssembledAtUtc = DateTimeOffset.UtcNow
    };

    private static Message CreateMessage(string id, string sessionId) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = sessionId,
        Role = "user",
        Content = "Sample content",
        TimestampUtc = DateTimeOffset.UtcNow
    };
}
