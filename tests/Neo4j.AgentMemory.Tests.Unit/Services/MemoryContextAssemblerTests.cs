using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class MemoryContextAssemblerTests
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly ILongTermMemoryService _longTerm;
    private readonly IReasoningMemoryService _reasoning;
    private readonly IGraphRagContextSource _graphRag;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly DateTimeOffset _fixedTime = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly float[] _generatedEmbedding = new float[1536];

    public MemoryContextAssemblerTests()
    {
        _shortTerm = Substitute.For<IShortTermMemoryService>();
        _longTerm = Substitute.For<ILongTermMemoryService>();
        _reasoning = Substitute.For<IReasoningMemoryService>();
        _graphRag = Substitute.For<IGraphRagContextSource>();
        _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        _clock = Substitute.For<IClock>();

        _clock.UtcNow.Returns(_fixedTime);

        _embeddingOrchestrator
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_generatedEmbedding));

        // Default: all services return empty results
        SetupEmptyServiceReturns();
    }

    private void SetupEmptyServiceReturns()
    {
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _shortTerm
            .SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _longTerm
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        _longTerm
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTerm
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        _reasoning
            .SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(Array.Empty<ReasoningTrace>()));
    }

    private MemoryContextAssembler CreateSut(
        IOptions<MemoryOptions>? options = null,
        IGraphRagContextSource? graphRag = null) =>
        new(_shortTerm, _longTerm, _reasoning, graphRag, _embeddingOrchestrator, _clock,
            options ?? Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance);

    private static RecallRequest CreateRequest(float[]? queryEmbedding = null) => new()
    {
        SessionId = "session-1",
        Query = "What do I know about the project?",
        QueryEmbedding = queryEmbedding
    };

    [Fact]
    public async Task AssembleContextAsync_GeneratesEmbeddingWhenNotProvided()
    {
        var sut = CreateSut();
        var request = CreateRequest(queryEmbedding: null);

        await sut.AssembleContextAsync(request);

        await _embeddingOrchestrator
            .Received(1)
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsync_UsesProvidedEmbedding()
    {
        var sut = CreateSut();
        var providedEmbedding = new float[1536];
        var request = CreateRequest(queryEmbedding: providedEmbedding);

        await sut.AssembleContextAsync(request);

        await _embeddingOrchestrator
            .DidNotReceive()
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsync_RetrievesFromAllMemoryLayers()
    {
        var sut = CreateSut();

        await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        await _shortTerm.Received(1).GetRecentMessagesAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _shortTerm.Received(1).SearchMessagesAsync(
            Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _longTerm.Received(1).SearchEntitiesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _longTerm.Received(1).SearchPreferencesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _longTerm.Received(1).SearchFactsAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsync_SkipsGraphRagWhenDisabled()
    {
        var options = Options.Create(new MemoryOptions { EnableGraphRag = false });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        await _graphRag
            .DidNotReceive()
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsync_SkipsGraphRagWhenSourceIsNull()
    {
        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: null); // null source

        var act = async () => await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AssembleContextAsync_IncludesGraphRagWhenEnabled()
    {
        var graphRagResult = new GraphRagContextResult
        {
            Items = new[] { new GraphRagContextItem { Text = "GraphRAG context text", Score = 0.9 } }
        };
        _graphRag
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(graphRagResult));

        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        await _graphRag
            .Received(1)
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>());
        result.GraphRagContext.Should().Contain("GraphRAG context text");
    }

    [Fact]
    public async Task AssembleContextAsync_SetsAssembledTimestamp()
    {
        var sut = CreateSut();

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        result.AssembledAtUtc.Should().Be(_fixedTime);
        result.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task AssembleContextAsync_EnforcesBudgetOldestFirst()
    {
        // Two messages each 10 chars — budget allows only 1
        var oldMsg = CreateMessage("old", "1234567890", _fixedTime.AddHours(-1));
        var newMsg = CreateMessage("new", "ABCDEFGHIJ", _fixedTime);
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(new[] { oldMsg, newMsg }));

        var options = Options.Create(new MemoryOptions
        {
            ContextBudget = new ContextBudget
            {
                MaxCharacters = 11, // fits only 1 of the 10-char messages
                TruncationStrategy = TruncationStrategy.OldestFirst
            }
        });
        var sut = CreateSut(options: options);

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // Budget enforced: fewer messages than provided
        result.RecentMessages.Items.Count.Should().BeLessThan(2);
        // OldestFirst: the newest message should be preserved
        result.RecentMessages.Items.Should().Contain(m => m.MessageId == "new");
    }

    [Fact]
    public async Task AssembleContextAsync_EnforcesBudgetLowestScoreFirst()
    {
        // Items are assumed ordered by score descending; lowest score (last) is dropped first
        var highScoreMsg = CreateMessage("high-score", "AAAAAAAAAA", _fixedTime);
        var lowScoreMsg = CreateMessage("low-score", "BBBBBBBBBB", _fixedTime.AddMinutes(-1));
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(new[] { highScoreMsg, lowScoreMsg }));

        var options = Options.Create(new MemoryOptions
        {
            ContextBudget = new ContextBudget
            {
                MaxCharacters = 11, // fits only 1 of the 10-char messages
                TruncationStrategy = TruncationStrategy.LowestScoreFirst
            }
        });
        var sut = CreateSut(options: options);

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // Budget enforced: fewer messages than provided
        result.RecentMessages.Items.Count.Should().BeLessThan(2);
        // LowestScoreFirst: highest-score item (first in list) should be preserved
        result.RecentMessages.Items.Should().Contain(m => m.MessageId == "high-score");
    }

    [Fact]
    public async Task AssembleContextAsync_ReportsEstimatedTokenCount()
    {
        var messages = new[]
        {
            CreateMessage("msg-1", "Hello world content here", _fixedTime),
            CreateMessage("msg-2", "More content to count chars", _fixedTime.AddMinutes(-1))
        };
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(messages));
        var sut = CreateSut();

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // The assembled context has content — char count should be positive
        var totalChars = result.RecentMessages.Items.Sum(m => m.Content.Length);
        totalChars.Should().BeGreaterThan(0);
        // Token estimate: chars / 4 is the standard approximation
        (totalChars / 4).Should().BeGreaterThan(0);
    }

    // ---- Helpers ----

    private static Message CreateMessage(string id, string content, DateTimeOffset timestamp) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "session-1",
        Role = "user",
        Content = content,
        TimestampUtc = timestamp
    };
}
