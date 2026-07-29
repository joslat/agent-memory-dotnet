using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class PersistenceStageBatchTests
{
    [Fact]
    public async Task PersistAsync_BatchCapableRepositories_UseOneBatchPerMemoryKind()
    {
        var entityRepository = Substitute.For<IEntityRepository, IBatchMemoryRepository<Entity>>();
        var factRepository = Substitute.For<IFactRepository, IBatchMemoryRepository<Fact>>();
        var preferenceRepository = Substitute.For<IPreferenceRepository, IBatchMemoryRepository<Preference>>();
        var relationshipRepository = Substitute.For<IRelationshipRepository, IBatchMemoryRepository<Relationship>>();
        var embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        var clock = Substitute.For<IClock>();
        var idGenerator = Substitute.For<IIdGenerator>();

        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-07-29T00:00:00Z"));
        idGenerator.GenerateId().Returns(
            "fact-1", "fact-2",
            "preference-1", "preference-2",
            "relationship-1", "relationship-2");
        embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[4]);

        var entityBatch = (IBatchMemoryRepository<Entity>)entityRepository;
        entityBatch.UpsertBatchAsync(Arg.Any<IReadOnlyList<Entity>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<Entity>>());
        var factBatch = (IBatchMemoryRepository<Fact>)factRepository;
        factBatch.UpsertBatchAsync(Arg.Any<IReadOnlyList<Fact>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<Fact>>());
        var preferenceBatch = (IBatchMemoryRepository<Preference>)preferenceRepository;
        preferenceBatch.UpsertBatchAsync(Arg.Any<IReadOnlyList<Preference>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<Preference>>());
        var relationshipBatch = (IBatchMemoryRepository<Relationship>)relationshipRepository;
        relationshipBatch.UpsertBatchAsync(Arg.Any<IReadOnlyList<Relationship>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<Relationship>>());

        var sut = new PersistenceStage(
            embeddingOrchestrator,
            entityRepository,
            factRepository,
            preferenceRepository,
            relationshipRepository,
            clock,
            idGenerator,
            NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions()));

        var extraction = new ExtractionStageResult
        {
            SourceMessageIds = ["message-1"],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = Entity("entity-1", "Alice"),
                ["Bob"] = Entity("entity-2", "Bob")
            },
            FilteredFacts =
            [
                new ExtractedFact
                {
                    Subject = "Alice",
                    Predicate = "likes",
                    Object = "coffee",
                    Confidence = 0.9
                },
                new ExtractedFact
                {
                    Subject = "Bob",
                    Predicate = "likes",
                    Object = "tea",
                    Confidence = 0.8
                }
            ],
            FilteredPreferences =
            [
                new ExtractedPreference { Category = "drink", PreferenceText = "coffee", Confidence = 0.9 },
                new ExtractedPreference { Category = "drink", PreferenceText = "tea", Confidence = 0.8 }
            ],
            FilteredRelationships =
            [
                new ExtractedRelationship
                {
                    SourceEntity = "Alice",
                    TargetEntity = "Bob",
                    RelationshipType = "KNOWS",
                    Confidence = 0.9
                },
                new ExtractedRelationship
                {
                    SourceEntity = "Bob",
                    TargetEntity = "Alice",
                    RelationshipType = "WORKS_WITH",
                    Confidence = 0.8
                }
            ]
        };

        var result = await sut.PersistAsync(extraction, ownerId: "owner-1");

        await entityBatch.Received(1).UpsertBatchAsync(
            Arg.Is<IReadOnlyList<Entity>>(items => items.Count == 2),
            Arg.Any<CancellationToken>());
        await factBatch.Received(1).UpsertBatchAsync(
            Arg.Is<IReadOnlyList<Fact>>(items => items.Count == 2),
            Arg.Any<CancellationToken>());
        await preferenceBatch.Received(1).UpsertBatchAsync(
            Arg.Is<IReadOnlyList<Preference>>(items => items.Count == 2),
            Arg.Any<CancellationToken>());
        await relationshipBatch.Received(1).UpsertBatchAsync(
            Arg.Is<IReadOnlyList<Relationship>>(items => items.Count == 2),
            Arg.Any<CancellationToken>());
        await entityRepository.DidNotReceive().UpsertAsync(
            Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await factRepository.DidNotReceive().UpsertAsync(
            Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        await preferenceRepository.DidNotReceive().UpsertAsync(
            Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        await relationshipRepository.DidNotReceive().UpsertAsync(
            Arg.Any<Relationship>(), Arg.Any<CancellationToken>());

        result.EntityCount.Should().Be(2);
        result.FactCount.Should().Be(2);
        result.PreferenceCount.Should().Be(2);
        result.RelationshipCount.Should().Be(2);
        result.Outcomes.Count(outcome =>
            outcome.Status == IngestionItemStatus.Succeeded).Should().Be(8);
    }

    private static Entity Entity(string id, string name) => new()
    {
        EntityId = id,
        Name = name,
        Type = "Person",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.Parse("2026-07-29T00:00:00Z")
    };
}