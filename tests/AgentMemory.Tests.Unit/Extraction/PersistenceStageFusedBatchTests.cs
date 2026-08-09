using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class PersistenceStageFusedBatchTests
{
    [Fact]
    public async Task PersistAsync_CoalescingEnabled_UsesFusedBatchForEverySupportedKindIncludingSingletons()
    {
        var entities = Substitute.For<IEntityRepository, IFusedBatchMemoryRepository<Entity>>();
        var facts = Substitute.For<IFactRepository, IFusedBatchMemoryRepository<Fact>>();
        var preferences = Substitute.For<IPreferenceRepository, IFusedBatchMemoryRepository<Preference>>();
        var relationships = Substitute.For<IRelationshipRepository>();

        var entityFused = (IFusedBatchMemoryRepository<Entity>)entities;
        var factFused = (IFusedBatchMemoryRepository<Fact>)facts;
        var preferenceFused = (IFusedBatchMemoryRepository<Preference>)preferences;
        entityFused.UpsertFusedBatchAsync(Arg.Any<IReadOnlyList<Entity>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<Entity>>());
        factFused.UpsertFusedBatchAsync(Arg.Any<IReadOnlyList<Fact>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<Fact>>());
        preferenceFused.UpsertFusedBatchAsync(Arg.Any<IReadOnlyList<Preference>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<Preference>>());
        relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Relationship>());

        var result = await CreateStage(entities, facts, preferences, relationships, enabled: true)
            .PersistAsync(Extraction(), ownerId: "owner-1");

        await entityFused.Received(1).UpsertFusedBatchAsync(
            Arg.Is<IReadOnlyList<Entity>>(items => items.Count == 2), Arg.Any<CancellationToken>());
        await factFused.Received(1).UpsertFusedBatchAsync(
            Arg.Is<IReadOnlyList<Fact>>(items => items.Count == 1), Arg.Any<CancellationToken>());
        await preferenceFused.Received(1).UpsertFusedBatchAsync(
            Arg.Is<IReadOnlyList<Preference>>(items => items.Count == 1), Arg.Any<CancellationToken>());
        await entities.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await facts.DidNotReceive().UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        await preferences.DidNotReceive().UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        result.EntityCount.Should().Be(2);
        result.FactCount.Should().Be(1);
        result.PreferenceCount.Should().Be(1);
        result.RelationshipCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_CoalescingDisabled_DoesNotUseFusedCapability()
    {
        var entities = Substitute.For<IEntityRepository, IFusedBatchMemoryRepository<Entity>>();
        var facts = Substitute.For<IFactRepository, IFusedBatchMemoryRepository<Fact>>();
        var preferences = Substitute.For<IPreferenceRepository, IFusedBatchMemoryRepository<Preference>>();
        var relationships = Substitute.For<IRelationshipRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());
        facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Fact>());
        preferences.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Preference>());
        relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Relationship>());

        await CreateStage(entities, facts, preferences, relationships, enabled: false)
            .PersistAsync(Extraction(), ownerId: "owner-1");

        await ((IFusedBatchMemoryRepository<Entity>)entities).DidNotReceiveWithAnyArgs()
            .UpsertFusedBatchAsync(default!, default);
        await ((IFusedBatchMemoryRepository<Fact>)facts).DidNotReceiveWithAnyArgs()
            .UpsertFusedBatchAsync(default!, default);
        await ((IFusedBatchMemoryRepository<Preference>)preferences).DidNotReceiveWithAnyArgs()
            .UpsertFusedBatchAsync(default!, default);
    }

    private static PersistenceStage CreateStage(
        IEntityRepository entities,
        IFactRepository facts,
        IPreferenceRepository preferences,
        IRelationshipRepository relationships,
        bool enabled)
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 1.0f, 0.0f });
        embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<string>>()
                .Select(_ => new float[] { 1.0f, 0.0f }).ToArray());
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-08-04T12:00:00Z"));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns("fact-1", "preference-1", "relationship-1");

        return new PersistenceStage(
            embeddings,
            entities,
            facts,
            preferences,
            relationships,
            clock,
            ids,
            NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions
            {
                FailureMode = IngestionFailureMode.BestEffort,
                EnableBatchMemoryUpserts = true,
                UseCoalescedPersistenceTransactions = enabled,
            }));
    }

    private static ExtractionStageResult Extraction() => new()
    {
        SourceMessageIds = [],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alice"] = Entity("entity-1", "Alice"),
            ["Bob"] = Entity("entity-2", "Bob"),
        },
        FilteredFacts =
        [
            new ExtractedFact
            {
                Subject = "Alice",
                Predicate = "likes",
                Object = "coffee",
                Confidence = 0.9,
            },
        ],
        FilteredPreferences =
        [
            new ExtractedPreference
            {
                Category = "drink",
                PreferenceText = "coffee",
                Confidence = 0.9,
            },
        ],
        FilteredRelationships =
        [
            new ExtractedRelationship
            {
                SourceEntity = "Alice",
                TargetEntity = "Bob",
                RelationshipType = "KNOWS",
                Confidence = 0.9,
            },
        ],
    };

    private static Entity Entity(string id, string name) => new()
    {
        EntityId = id,
        Name = name,
        Type = "Person",
        Confidence = 0.9,
        Embedding = [1.0f, 0.0f],
        CreatedAtUtc = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
    };
}
