using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Unit tests for <see cref="PersistenceStage"/>:
/// embedding generation, repository upserts, EXTRACTED_FROM provenance wiring,
/// and fault-tolerance during persistence.
/// </summary>
public sealed class PersistenceStageTests
{
    private readonly IEmbeddingOrchestrator _orchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IEntityRepository _entityRepo = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _factRepo = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _prefRepo = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private static readonly IReadOnlyList<string> TwoMessageIds = new[] { "msg-1", "msg-2" };

    public PersistenceStageTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        _orchestrator.EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);
        _orchestrator.EmbedFactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);
        _orchestrator.EmbedPreferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        _entityRepo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        _factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _prefRepo.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Preference>()));
        _relRepo.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Relationship>()));
    }

    private PersistenceStage CreateSut() =>
        new(_orchestrator, _entityRepo, _factRepo, _prefRepo, _relRepo, _clock, _idGen,
            NullLogger<PersistenceStage>.Instance);

    private static ExtractionStageResult EmptyResult(IReadOnlyList<string>? sourceIds = null) =>
        new()
        {
            SourceMessageIds = sourceIds ?? TwoMessageIds
        };

    // ── Entity persistence ──

    [Fact]
    public async Task PersistAsync_Entity_EmbedsAndUpserts()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = entity
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        await _orchestrator.Received(1).EmbedEntityAsync("Alice", Arg.Any<CancellationToken>());
        await _entityRepo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        result.EntityCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_Entity_SkipsEmbeddingWhenAlreadyPresent()
    {
        var entityWithEmbedding = new Entity
        {
            EntityId = "e-1",
            Name = "Alice",
            Type = "Person",
            Confidence = 0.9,
            Embedding = new float[384], // already embedded
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = entityWithEmbedding
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _orchestrator.DidNotReceive().EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Entity_CreatesExtractedFromForEachSourceMessage()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = entity
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        // 2 source messages → 2 EXTRACTED_FROM calls
        await _entityRepo.Received(TwoMessageIds.Count).CreateExtractedFromRelationshipAsync(
            "e-1", Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Fact persistence ──

    [Fact]
    public async Task PersistAsync_Fact_EmbedsAndUpserts()
    {
        _idGen.GenerateId().Returns("fact-1");

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[]
            {
                new ExtractedFact { Subject = "Alice", Predicate = "works_at", Object = "Acme", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        await _orchestrator.Received(1).EmbedFactAsync("Alice", "works_at", "Acme", Arg.Any<CancellationToken>());
        await _factRepo.Received(1).UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        result.FactCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_Fact_CreatesExtractedFromForEachSourceMessage()
    {
        _idGen.GenerateId().Returns("fact-1");

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[]
            {
                new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "coffee", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _factRepo.Received(TwoMessageIds.Count).CreateExtractedFromRelationshipAsync(
            "fact-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Preference persistence ──

    [Fact]
    public async Task PersistAsync_Preference_EmbedsAndUpserts()
    {
        _idGen.GenerateId().Returns("pref-1");

        var extraction = EmptyResult() with
        {
            FilteredPreferences = new[]
            {
                new ExtractedPreference { Category = "style", PreferenceText = "dark mode", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        await _orchestrator.Received(1).EmbedPreferenceAsync("dark mode", Arg.Any<CancellationToken>());
        await _prefRepo.Received(1).UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        result.PreferenceCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_Preference_CreatesExtractedFromForEachSourceMessage()
    {
        _idGen.GenerateId().Returns("pref-1");

        var extraction = EmptyResult() with
        {
            FilteredPreferences = new[]
            {
                new ExtractedPreference { Category = "style", PreferenceText = "dark mode", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _prefRepo.Received(TwoMessageIds.Count).CreateExtractedFromRelationshipAsync(
            "pref-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Relationship persistence ──

    [Fact]
    public async Task PersistAsync_Relationship_ResolvesEntityIdsFromPersistedMap()
    {
        _idGen.GenerateId().Returns("rel-1");

        var alice = new Entity { EntityId = "e-alice", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var acme = new Entity { EntityId = "e-acme", Name = "Acme", Type = "Organization", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = alice,
                ["Acme"] = acme
            },
            FilteredRelationships = new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", TargetEntity = "Acme", RelationshipType = "WORKS_FOR", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _relRepo.Received(1).UpsertAsync(
            Arg.Is<Relationship>(r =>
                r.SourceEntityId == "e-alice" &&
                r.TargetEntityId == "e-acme" &&
                r.RelationshipType == "WORKS_FOR"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Relationship_SkippedWhenEntityNotInPersistedMap()
    {
        // No entities in the resolved map, so relationship can't be wired
        var extraction = EmptyResult() with
        {
            FilteredRelationships = new[]
            {
                new ExtractedRelationship { SourceEntity = "Ghost", TargetEntity = "Nobody", RelationshipType = "KNOWS", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _relRepo.DidNotReceive().UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
    }

    // ── Fault tolerance ──

    [Fact]
    public async Task PersistAsync_EntityUpsertFails_ContinuesToNextEntity()
    {
        var alice = new Entity { EntityId = "e-alice", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var bob = new Entity { EntityId = "e-bob", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var callCount = 0;
        _entityRepo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++callCount == 1) throw new InvalidOperationException("DB error");
                return Task.FromResult(_.Arg<Entity>());
            });

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = alice,
                ["Bob"] = bob
            }
        };

        var sut = CreateSut();
        // Should not throw
        await sut.PersistAsync(extraction);

        await _entityRepo.Received(2).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_ExtractedFromFails_DoesNotFailPersistence()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        _entityRepo.CreateExtractedFromRelationshipAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("DB connection failed")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bob"] = entity
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        // Entity still counted as persisted even though provenance failed
        result.EntityCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_EmptyExtraction_ReturnsZeroCounts()
    {
        var sut = CreateSut();
        var result = await sut.PersistAsync(EmptyResult());

        result.EntityCount.Should().Be(0);
        result.FactCount.Should().Be(0);
        result.PreferenceCount.Should().Be(0);
        result.RelationshipCount.Should().Be(0);
    }
}
