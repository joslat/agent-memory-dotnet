using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class ShortTermMemoryServiceTests
{
    private readonly IConversationRepository _conversationRepo;
    private readonly IMessageRepository _messageRepo;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly DateTimeOffset _fixedTime = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public ShortTermMemoryServiceTests()
    {
        _conversationRepo = Substitute.For<IConversationRepository>();
        _messageRepo = Substitute.For<IMessageRepository>();
        _embeddingGenerator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _clock.UtcNow.Returns(_fixedTime);
        _idGenerator.GenerateId().Returns("generated-id-1", "generated-id-2", "generated-id-3");
        _embeddingGenerator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MockFactory.EmbeddingResult(call, new float[1536]));

        _conversationRepo
            .UpsertAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Conversation>()));

        _messageRepo
            .AddAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Message>()));

        _messageRepo
            .AddBatchAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Message>>(ci.Arg<IEnumerable<Message>>().ToList()));
    }

    private ShortTermMemoryService CreateSut(IOptions<ShortTermMemoryOptions>? options = null) =>
        new(_conversationRepo, _messageRepo, _embeddingGenerator, _clock, _idGenerator,
            options ?? Options.Create(new ShortTermMemoryOptions()),
            NullLogger<ShortTermMemoryService>.Instance);

    [Fact]
    public async Task AddConversationAsync_CreatesConversationWithGeneratedId()
    {
        var sut = CreateSut();

        var result = await sut.AddConversationAsync("conv-1", "session-1");

        result.ConversationId.Should().Be("conv-1");
        result.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task AddConversationAsync_SetsTimestampsFromClock()
    {
        var sut = CreateSut();

        var result = await sut.AddConversationAsync("conv-1", "session-1");

        result.CreatedAtUtc.Should().Be(_fixedTime);
        result.UpdatedAtUtc.Should().Be(_fixedTime);
    }

    [Fact]
    public async Task AddConversationAsync_UpsertsDelegatesToRepository()
    {
        var sut = CreateSut();

        await sut.AddConversationAsync("conv-1", "session-1");

        await _conversationRepo
            .Received(1)
            .UpsertAsync(Arg.Is<Conversation>(c => c.ConversationId == "conv-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMessageAsync_GeneratesEmbeddingWhenEnabled()
    {
        var sut = CreateSut(Options.Create(new ShortTermMemoryOptions { GenerateEmbeddings = true }));
        var message = CreateMessage("msg-1", withEmbedding: false);

        await sut.AddMessageAsync(message);

        await _embeddingGenerator
            .Received(1)
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMessageAsync_SkipsEmbeddingWhenDisabled()
    {
        var sut = CreateSut(Options.Create(new ShortTermMemoryOptions { GenerateEmbeddings = false }));
        var message = CreateMessage("msg-1", withEmbedding: false);

        await sut.AddMessageAsync(message);

        await _embeddingGenerator
            .DidNotReceive()
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMessageAsync_SkipsEmbeddingWhenAlreadyProvided()
    {
        var sut = CreateSut(Options.Create(new ShortTermMemoryOptions { GenerateEmbeddings = true }));
        var message = CreateMessage("msg-1", withEmbedding: true);

        await sut.AddMessageAsync(message);

        await _embeddingGenerator
            .DidNotReceive()
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMessageAsync_DelegatesToRepository()
    {
        var sut = CreateSut();
        var message = CreateMessage("msg-1");

        await sut.AddMessageAsync(message);

        await _messageRepo.Received(1).AddAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMessagesAsync_EmbedsEachMessage()
    {
        var sut = CreateSut(Options.Create(new ShortTermMemoryOptions { GenerateEmbeddings = true }));
        var messages = new[]
        {
            CreateMessage("msg-1", withEmbedding: false),
            CreateMessage("msg-2", withEmbedding: false),
            CreateMessage("msg-3", withEmbedding: false),
        };

        await sut.AddMessagesAsync(messages);

        await _embeddingGenerator
            .Received(3)
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRecentMessagesAsync_DelegatesToRepository()
    {
        _messageRepo
            .GetRecentBySessionAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        var sut = CreateSut();

        await sut.GetRecentMessagesAsync("session-1", 5);

        await _messageRepo
            .Received(1)
            .GetRecentBySessionAsync("session-1", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRecentMessagesAsync_CapsAtMaxMessagesPerQuery()
    {
        const int maxPerQuery = 50;
        _messageRepo
            .GetRecentBySessionAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        var sut = CreateSut(Options.Create(new ShortTermMemoryOptions { MaxMessagesPerQuery = maxPerQuery }));

        await sut.GetRecentMessagesAsync("session-1", 9999);

        await _messageRepo
            .Received(1)
            .GetRecentBySessionAsync("session-1", maxPerQuery, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMessagesAsync_DelegatesToRepositoryAndStripsScores()
    {
        var message = CreateMessage("msg-1");
        _messageRepo
            .SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Message, double)>>(new[] { (message, 0.95) }));
        var sut = CreateSut();

        var result = await sut.SearchMessagesAsync("session-1", new float[1536]);

        result.Should().ContainSingle();
        result[0].Should().Be(message);
    }

    [Fact]
    public async Task ClearSessionAsync_DeletesMessagesAndConversations()
    {
        var conversations = new[] { CreateConversation("conv-1", "session-1") };
        _conversationRepo
            .GetBySessionAsync("session-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Conversation>>(conversations));
        _messageRepo
            .DeleteBySessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _conversationRepo
            .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sut = CreateSut();

        await sut.ClearSessionAsync("session-1");

        await _messageRepo.Received(1).DeleteBySessionAsync("session-1", Arg.Any<CancellationToken>());
        await _conversationRepo.Received(1).DeleteAsync("conv-1", Arg.Any<CancellationToken>());
    }

    private static Message CreateMessage(string id, bool withEmbedding = false) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "session-1",
        Role = "user",
        Content = $"Content for {id}",
        TimestampUtc = DateTimeOffset.UtcNow,
        Embedding = withEmbedding ? new float[1536] : null
    };

    private static Conversation CreateConversation(string conversationId, string sessionId) => new()
    {
        ConversationId = conversationId,
        SessionId = sessionId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };
}
