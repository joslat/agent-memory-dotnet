using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class TemporalContextAssemblerTests
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly ILongTermMemoryService _longTerm;
    private readonly IReasoningMemoryService _reasoning;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly DateTimeOffset _fixedTime = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly float[] _generatedEmbedding = new float[1536];

    public TemporalContextAssemblerTests()
    {
        _shortTerm = Substitute.For<IShortTermMemoryService>();
        _longTerm = Substitute.For<ILongTermMemoryService>();
        _reasoning = Substitute.For<IReasoningMemoryService>();
        _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        _clock = Substitute.For<IClock>();

        _clock.UtcNow.Returns(_fixedTime);

        _embeddingOrchestrator
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_generatedEmbedding));

        SetupEmptyServiceReturns();
    }

    private void SetupEmptyServiceReturns()
    {
        _shortTerm
            .GetRecentMessagesAsOfAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _longTerm
            .SearchEntitiesAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        _longTerm
            .SearchFactsAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        _longTerm
            .SearchPreferencesAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
    }

    private MemoryContextAssembler CreateSut(IOptions<MemoryOptions>? options = null) =>
        new(_shortTerm, _longTerm, _reasoning, null,
            _embeddingOrchestrator, _clock,
            options ?? Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance);

    [Fact]
    public async Task AssembleContextAsOfAsync_ReturnsContextWithSessionId()
    {
        var asOf = _fixedTime.AddDays(-7);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "session-1", Query = "What happened?" };

        var context = await sut.AssembleContextAsOfAsync(request, asOf);

        context.Should().NotBeNull();
        context.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_PassesAsOfToShortTermService()
    {
        var asOf = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "session-1", Query = "test" };

        await sut.AssembleContextAsOfAsync(request, asOf);

        await _shortTerm.Received(1).GetRecentMessagesAsOfAsync(
            "session-1", asOf, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_PassesAsOfToLongTermEntities()
    {
        var asOf = _fixedTime.AddDays(-30);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "test" };

        await sut.AssembleContextAsOfAsync(request, asOf);

        await _longTerm.Received(1).SearchEntitiesAsOfAsync(
            Arg.Any<float[]>(), asOf, Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_PassesAsOfToLongTermFacts()
    {
        var asOf = _fixedTime.AddDays(-15);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "test" };

        await sut.AssembleContextAsOfAsync(request, asOf);

        await _longTerm.Received(1).SearchFactsAsOfAsync(
            Arg.Any<float[]>(), asOf, Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_PassesAsOfToLongTermPreferences()
    {
        var asOf = _fixedTime.AddDays(-20);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "test" };

        await sut.AssembleContextAsOfAsync(request, asOf);

        await _longTerm.Received(1).SearchPreferencesAsOfAsync(
            Arg.Any<float[]>(), asOf, Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_GeneratesEmbeddingWhenNotProvided()
    {
        var asOf = _fixedTime.AddDays(-1);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "What do I know?" };

        await sut.AssembleContextAsOfAsync(request, asOf);

        await _embeddingOrchestrator.Received(1).EmbedQueryAsync("What do I know?", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_UsesProvidedEmbedding()
    {
        var asOf = _fixedTime.AddDays(-1);
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "test", QueryEmbedding = embedding };

        await sut.AssembleContextAsOfAsync(request, asOf);

        await _embeddingOrchestrator.DidNotReceive().EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_IncludesAsOfInMetadata()
    {
        var asOf = _fixedTime.AddDays(-3);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "test" };

        var context = await sut.AssembleContextAsOfAsync(request, asOf);

        context.Metadata.Should().ContainKey("asOf");
        context.Metadata["asOf"].Should().Be(asOf);
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_ReturnsEntitiesFromLongTerm()
    {
        var asOf = _fixedTime.AddDays(-5);
        var entities = new[]
        {
            CreateEntity("e1"),
            CreateEntity("e2")
        };

        _longTerm
            .SearchEntitiesAsOfAsync(Arg.Any<float[]>(), asOf, Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(entities));

        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "test" };

        var context = await sut.AssembleContextAsOfAsync(request, asOf);

        context.RelevantEntities.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_DoesNotIncludeRelevantMessagesOrTraces()
    {
        var asOf = _fixedTime.AddDays(-5);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "test" };

        var context = await sut.AssembleContextAsOfAsync(request, asOf);

        // Temporal recall omits vector-searched messages and traces
        context.RelevantMessages.Items.Should().BeEmpty();
        context.SimilarTraces.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_SetsAssembledAtUtcToCurrent()
    {
        var asOf = _fixedTime.AddDays(-10);
        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "test" };

        var context = await sut.AssembleContextAsOfAsync(request, asOf);

        context.AssembledAtUtc.Should().Be(_fixedTime);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Entity CreateEntity(string id) => new()
    {
        EntityId = id,
        Name = $"Entity {id}",
        Type = "PERSON",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };
}
