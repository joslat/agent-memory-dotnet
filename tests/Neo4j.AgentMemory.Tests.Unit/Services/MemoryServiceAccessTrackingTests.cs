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

public sealed class MemoryServiceAccessTrackingTests
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

    public MemoryServiceAccessTrackingTests()
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

        _decayService
            .UpdateAccessTimestampAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private MemoryService CreateSut(IMemoryDecayService? decay = null) =>
        new(_shortTerm, _assembler, _extractionPipeline,
            _entityRepository, _factRepository, _preferenceRepository, _embeddingOrchestrator,
            Options.Create(new MemoryOptions()),
            _clock, _idGenerator,
            NullLogger<MemoryService>.Instance,
            decay);

    [Fact]
    public async Task RecallAsync_WithDecayService_UpdatesEntityAccess()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = new[]
                {
                    CreateEntity("ent-1"),
                    CreateEntity("ent-2")
                }
            }
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });

        // Give fire-and-forget time to execute
        await Task.Delay(100);

        await _decayService.Received().UpdateAccessTimestampAsync("ent-1", "Entity", Arg.Any<CancellationToken>());
        await _decayService.Received().UpdateAccessTimestampAsync("ent-2", "Entity", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsync_WithDecayService_UpdatesFactAccess()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = new[] { CreateFact("fact-1") }
            }
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });
        await Task.Delay(100);

        await _decayService.Received().UpdateAccessTimestampAsync("fact-1", "Fact", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsync_WithDecayService_UpdatesPreferenceAccess()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantPreferences = new MemoryContextSection<Preference>
            {
                Items = new[] { CreatePreference("pref-1") }
            }
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });
        await Task.Delay(100);

        await _decayService.Received().UpdateAccessTimestampAsync("pref-1", "Preference", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsync_WithoutDecayService_StillWorks()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = new[] { CreateEntity("ent-1") }
            }
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        // No decay service
        var sut = CreateSut(null);
        var result = await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });

        result.Should().NotBeNull();
        result.Context.RelevantEntities.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task RecallAsync_EmptyContext_NoAccessTracking()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });
        await Task.Delay(100);

        await _decayService.DidNotReceive()
            .UpdateAccessTimestampAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
