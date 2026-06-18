using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class TemporalRecallTests
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly IMemoryContextAssembler _assembler;
    private readonly IMemoryExtractionPipeline _extractionPipeline;
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IMemoryDecayService _decayService;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly DateTimeOffset _fixedTime = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public TemporalRecallTests()
    {
        _shortTerm = Substitute.For<IShortTermMemoryService>();
        _assembler = Substitute.For<IMemoryContextAssembler>();
        _extractionPipeline = Substitute.For<IMemoryExtractionPipeline>();
        _entityRepository = Substitute.For<IEntityRepository>();
        _factRepository = Substitute.For<IFactRepository>();
        _preferenceRepository = Substitute.For<IPreferenceRepository>();
        _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        _decayService = Substitute.For<IMemoryDecayService>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _clock.UtcNow.Returns(_fixedTime);
        _idGenerator.GenerateId().Returns("generated-msg-id");
    }

    private MemoryService CreateSut(IOptions<MemoryOptions>? options = null) =>
        new(_shortTerm, _assembler, _extractionPipeline,
            _entityRepository, _factRepository, _preferenceRepository, _embeddingOrchestrator,
            options ?? Options.Create(new MemoryOptions()),
            _clock, _idGenerator,
            NullLogger<MemoryService>.Instance,
            _decayService);

    // ── RecallAsOfAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task RecallAsOfAsync_DelegatesToAssemblerWithAsOf()
    {
        var asOf = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var context = CreateEmptyContext("session-1");

        _assembler
            .AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), asOf, asOf, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "session-1", Query = "test" };

        var result = await sut.RecallAsOfAsync(request, asOf);

        result.Should().NotBeNull();
        result.Context.SessionId.Should().Be("session-1");
        // Single-clock recall delegates to the bitemporal overload with both clocks equal (D6).
        await _assembler.Received(1).AssembleContextAsOfAsync(request, asOf, asOf, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsOfAsync_ReturnsCorrectTotalItems()
    {
        var asOf = _fixedTime.AddDays(-10);
        var context = new MemoryContext
        {
            SessionId = "session-1",
            AssembledAtUtc = _fixedTime,
            RecentMessages = new MemoryContextSection<Message>
            {
                Items = new[] { CreateMessage("msg-1", "session-1") }
            },
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = new[] { CreateEntity("ent-1"), CreateEntity("ent-2") }
            },
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = new[] { CreateFact("fact-1") }
            },
            RelevantPreferences = new MemoryContextSection<Preference>
            {
                Items = new[] { CreatePreference("pref-1") }
            },
            // SimilarTraces is populated on the as-of path too, so it must be counted in the total.
            SimilarTraces = new MemoryContextSection<ReasoningTrace>
            {
                Items = new[]
                {
                    new ReasoningTrace { TraceId = "trace-1", SessionId = "session-1", Task = "t", StartedAtUtc = _fixedTime }
                }
            }
        };

        _assembler
            .AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), asOf, asOf, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut();
        var result = await sut.RecallAsOfAsync(
            new RecallRequest { SessionId = "session-1", Query = "test" }, asOf);

        result.TotalItemsRetrieved.Should().Be(6); // 1 msg + 2 entities + 1 fact + 1 pref + 1 trace
    }

    [Fact]
    public async Task RecallAsOfAsync_IncludesAsOfInMetadata()
    {
        var asOf = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _assembler
            .AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), asOf, asOf, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateEmptyContext("s1")));

        var sut = CreateSut();
        var result = await sut.RecallAsOfAsync(
            new RecallRequest { SessionId = "s1", Query = "q" }, asOf);

        result.Metadata.Should().ContainKey("asOf");
        result.Metadata["asOf"].Should().Be(asOf);
    }

    [Fact]
    public async Task RecallAsOfAsync_DoesNotTriggerAccessTracking()
    {
        var asOf = _fixedTime.AddDays(-5);
        _assembler
            .AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), asOf, asOf, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateEmptyContext("s1")));

        var sut = CreateSut();
        await sut.RecallAsOfAsync(
            new RecallRequest { SessionId = "s1", Query = "q" }, asOf);

        // Temporal recall is read-only; should NOT update access timestamps.
        await _decayService.DidNotReceive()
            .UpdateAccessTimestampAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── RecallAsOfAsync (bitemporal, two-clock — D6) ─────────────────────

    [Fact]
    public async Task RecallAsOfAsync_TwoClock_PassesBothClocksToAssembler()
    {
        var validAsOf = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var systemAsOf = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero);
        _assembler
            .AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), validAsOf, systemAsOf, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateEmptyContext("s1")));

        var sut = CreateSut();
        var request = new RecallRequest { SessionId = "s1", Query = "q" };

        await sut.RecallAsOfAsync(request, validAsOf, systemAsOf);

        // The two clocks must reach the assembler distinctly — not collapsed to one.
        await _assembler.Received(1)
            .AssembleContextAsOfAsync(request, validAsOf, systemAsOf, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsOfAsync_TwoClock_RecordsBothClocksInMetadata()
    {
        var validAsOf = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var systemAsOf = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero);
        _assembler
            .AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), validAsOf, systemAsOf, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateEmptyContext("s1")));

        var sut = CreateSut();
        var result = await sut.RecallAsOfAsync(
            new RecallRequest { SessionId = "s1", Query = "q" }, validAsOf, systemAsOf);

        result.Metadata.Should().ContainKey("validAsOf").WhoseValue.Should().Be(validAsOf);
        result.Metadata.Should().ContainKey("systemAsOf").WhoseValue.Should().Be(systemAsOf);
        // "asOf" is the backward-compatible valid-time alias.
        result.Metadata["asOf"].Should().Be(validAsOf);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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

    private static Entity CreateEntity(string id) => new()
    {
        EntityId = id,
        Name = $"Entity {id}",
        Type = "PERSON",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static Fact CreateFact(string id) => new()
    {
        FactId = id,
        Subject = "Alice",
        Predicate = "works_at",
        Object = "ACME",
        Confidence = 0.8,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static Preference CreatePreference(string id) => new()
    {
        PreferenceId = id,
        Category = "style",
        PreferenceText = "Prefers dark mode",
        Confidence = 0.7,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };
}
