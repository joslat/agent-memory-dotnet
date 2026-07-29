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

public sealed class PersistenceStageBatchFailureTests
{
    [Fact]
    public async Task PersistAsync_BatchFailure_ReplaysItemPathAndPreservesOutcomes()
    {
        var entityRepository = Substitute.For<IEntityRepository, IBatchMemoryRepository<Entity>>();
        var batch = (IBatchMemoryRepository<Entity>)entityRepository;
        batch.UpsertBatchAsync(Arg.Any<IReadOnlyList<Entity>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Entity>>>(_ => throw new InvalidOperationException("batch failed"));
        entityRepository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());

        var sut = CreateSut(entityRepository, new ExtractionOptions());
        var result = await sut.PersistAsync(TwoEntities());

        await batch.Received(1).UpsertBatchAsync(
            Arg.Is<IReadOnlyList<Entity>>(items => items.Count == 2),
            Arg.Any<CancellationToken>());
        await entityRepository.Received(2).UpsertAsync(
            Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        result.EntityCount.Should().Be(2);
        result.Outcomes.Count(outcome =>
            outcome.Kind == MemoryItemKind.Entity &&
            outcome.Status == IngestionItemStatus.Succeeded).Should().Be(2);
    }

    [Fact]
    public async Task PersistAsync_BatchOptionDisabled_UsesItemPath()
    {
        var entityRepository = Substitute.For<IEntityRepository, IBatchMemoryRepository<Entity>>();
        var batch = (IBatchMemoryRepository<Entity>)entityRepository;
        entityRepository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());

        var sut = CreateSut(entityRepository, new ExtractionOptions
        {
            EnableBatchMemoryUpserts = false
        });
        var result = await sut.PersistAsync(TwoEntities());

        await batch.DidNotReceive().UpsertBatchAsync(
            Arg.Any<IReadOnlyList<Entity>>(), Arg.Any<CancellationToken>());
        await entityRepository.Received(2).UpsertAsync(
            Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        result.EntityCount.Should().Be(2);
    }

    [Fact]
    public async Task PersistAsync_FailFastMode_UsesItemPathForExactFailureAttribution()
    {
        var entityRepository = Substitute.For<IEntityRepository, IBatchMemoryRepository<Entity>>();
        var batch = (IBatchMemoryRepository<Entity>)entityRepository;
        entityRepository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());

        var sut = CreateSut(entityRepository, new ExtractionOptions
        {
            FailureMode = IngestionFailureMode.FailFast
        });
        var result = await sut.PersistAsync(TwoEntities());

        await batch.DidNotReceive().UpsertBatchAsync(
            Arg.Any<IReadOnlyList<Entity>>(), Arg.Any<CancellationToken>());
        await entityRepository.Received(2).UpsertAsync(
            Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        result.EntityCount.Should().Be(2);
    }

    private static PersistenceStage CreateSut(
        IEntityRepository entityRepository,
        ExtractionOptions options)
    {
        var embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[4]);

        return new PersistenceStage(
            embeddingOrchestrator,
            entityRepository,
            Substitute.For<IFactRepository>(),
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(),
            Substitute.For<IClock>(),
            Substitute.For<IIdGenerator>(),
            NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(options));
    }

    private static ExtractionStageResult TwoEntities() => new()
    {
        SourceMessageIds = ["message-1"],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alice"] = Entity("entity-1", "Alice"),
            ["Bob"] = Entity("entity-2", "Bob")
        }
    };

    private static Entity Entity(string id, string name) => new()
    {
        EntityId = id,
        Name = name,
        Type = "Person",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.Parse("2026-07-29T00:00:00Z")
    };
}
