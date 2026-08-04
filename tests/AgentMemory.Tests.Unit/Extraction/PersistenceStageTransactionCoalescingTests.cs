using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class PersistenceStageTransactionCoalescingTests
{
    [Fact]
    public async Task PersistAsync_BestEffortSuccess_CoalescesLogicalOperation()
    {
        var entityRepository = Substitute.For<IEntityRepository>();
        entityRepository
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());
        var transaction = new RecordingPersistenceTransaction();
        var stage = CreateStage(
            entityRepository,
            transaction,
            new ExtractionOptions
            {
                FailureMode = IngestionFailureMode.BestEffort,
                UseCoalescedPersistenceTransactions = true,
            });
        var extraction = EntityExtraction(withEmbedding: true);

        var result = await stage.PersistAsync(extraction, ownerId: "owner-1");

        result.EntityCount.Should().Be(1);
        transaction.ExecutionCount.Should().Be(1);
        transaction.IsOpen.Should().BeFalse();
        await entityRepository.Received(1)
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Coalescing_DefaultsOn()
    {
        new ExtractionOptions().UseCoalescedPersistenceTransactions.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PersistAsync_DisabledOrUnsupported_RetainsLegacyPath(
        bool optionEnabled,
        bool supportsAtomicRollback)
    {
        var entityRepository = SuccessfulEntityRepository();
        var transaction = new RecordingPersistenceTransaction(supportsAtomicRollback);
        var stage = CreateStage(
            entityRepository,
            transaction,
            new ExtractionOptions
            {
                FailureMode = IngestionFailureMode.BestEffort,
                UseCoalescedPersistenceTransactions = optionEnabled,
            });

        var result = await stage.PersistAsync(EntityExtraction(withEmbedding: true));

        result.EntityCount.Should().Be(1);
        transaction.ExecutionCount.Should().Be(0);
        await entityRepository.Received(1)
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_PreparesEmbeddingBeforeCoalescedTransaction()
    {
        var transaction = new RecordingPersistenceTransaction();
        var embedding = Substitute.For<IEmbeddingOrchestrator>();
        embedding
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 1.0f, 0.0f });
        embedding
            .When(provider => provider.EmbedEntityAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => transaction.IsOpen.Should().BeFalse());
        var entityRepository = SuccessfulEntityRepository();
        entityRepository
            .When(repository => repository.UpsertAsync(
                Arg.Any<Entity>(), Arg.Any<CancellationToken>()))
            .Do(_ => transaction.IsOpen.Should().BeTrue());
        var stage = CreateStage(
            entityRepository,
            transaction,
            new ExtractionOptions { FailureMode = IngestionFailureMode.BestEffort },
            embedding);

        await stage.PersistAsync(EntityExtraction(withEmbedding: false));

        transaction.ExecutionCount.Should().Be(1);
        await embedding.Received(1)
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_ItemFailure_RollsBackThenReplaysLegacyPath()
    {
        var attempts = 0;
        var entityRepository = Substitute.For<IEntityRepository>();
        entityRepository
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("injected first-attempt failure");
                return call.Arg<Entity>();
            });
        var transaction = new RecordingPersistenceTransaction();
        var stage = CreateStage(
            entityRepository,
            transaction,
            new ExtractionOptions { FailureMode = IngestionFailureMode.BestEffort });

        var result = await stage.PersistAsync(EntityExtraction(withEmbedding: true));

        result.Statuses().Should().NotContain(IngestionItemStatus.Failed);
        result.EntityCount.Should().Be(1);
        attempts.Should().Be(2);
        transaction.ExecutionCount.Should().Be(1);
        transaction.RollbackCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_FailFastTransactionBoundaryFailure_PreservesIngestionContract()
    {
        var entityRepository = SuccessfulEntityRepository();
        var transaction = new FailingAfterWorkPersistenceTransaction();
        var stage = CreateStage(
            entityRepository,
            transaction,
            new ExtractionOptions { FailureMode = IngestionFailureMode.FailFast });

        var act = () => stage.PersistAsync(EntityExtraction(withEmbedding: true));

        var assertion = await act.Should().ThrowAsync<MemoryIngestionException>();
        assertion.Which.InnerException.Should().BeOfType<AggregateException>()
            .Which.Message.Should().Contain("rollback could not be confirmed");
        transaction.ExecutionCount.Should().Be(1);
        transaction.WorkExecutionCount.Should().Be(1);
        await entityRepository.Received(1)
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_UncertainRollback_FailsClosedWithoutReplay()
    {
        var attempts = 0;
        var entityRepository = Substitute.For<IEntityRepository>();
        entityRepository
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                return Task.FromException<Entity>(new InvalidOperationException("injected write failure"));
            });
        var transaction = new RecordingPersistenceTransaction(failRollback: true);
        var stage = CreateStage(
            entityRepository,
            transaction,
            new ExtractionOptions { FailureMode = IngestionFailureMode.BestEffort });

        var act = () => stage.PersistAsync(EntityExtraction(withEmbedding: true));

        await act.Should().ThrowAsync<AggregateException>()
            .WithMessage("*rollback could not be confirmed*");
        attempts.Should().Be(1, "an uncertain transaction must never be replayed");
        transaction.ExecutionCount.Should().Be(1);
    }

    private static IEntityRepository SuccessfulEntityRepository()
    {
        var repository = Substitute.For<IEntityRepository>();
        repository
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());
        return repository;
    }

    private static ExtractionStageResult EntityExtraction(bool withEmbedding) => new()
    {
        SourceMessageIds = [],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alice"] = new()
            {
                EntityId = "entity-1",
                Name = "Alice",
                Type = "Person",
                Confidence = 0.99,
                Embedding = withEmbedding ? [1.0f, 0.0f] : null,
                CreatedAtUtc = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            },
        },
    };

    private static PersistenceStage CreateStage(
        IEntityRepository entityRepository,
        IMemoryPersistenceTransaction transaction,
        ExtractionOptions options,
        IEmbeddingOrchestrator? embedding = null)
    {
        embedding ??= Substitute.For<IEmbeddingOrchestrator>();
        var facts = Substitute.For<IFactRepository>();
        var preferences = Substitute.For<IPreferenceRepository>();
        var relationships = Substitute.For<IRelationshipRepository>();
        var clock = Substitute.For<IClock>();
        var ids = Substitute.For<IIdGenerator>();
        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-08-04T12:00:00Z"));
        return new PersistenceStage(
            embedding!,
            entityRepository,
            facts,
            preferences,
            relationships,
            clock,
            ids,
            NullLogger<PersistenceStage>.Instance,
            transaction,
            Options.Create(options));
    }

    private sealed class FailingAfterWorkPersistenceTransaction : IMemoryPersistenceTransaction
    {
        public bool SupportsAtomicRollback => true;
        public int ExecutionCount { get; private set; }
        public int WorkExecutionCount { get; private set; }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            _ = await work(cancellationToken);
            WorkExecutionCount++;
            throw new AggregateException(
                "Atomic persistence failed and rollback could not be confirmed.");
        }
    }

    private sealed class RecordingPersistenceTransaction : IMemoryPersistenceTransaction
    {
        private readonly bool _failRollback;

        public RecordingPersistenceTransaction(
            bool supportsAtomicRollback = true,
            bool failRollback = false)
        {
            SupportsAtomicRollback = supportsAtomicRollback;
            _failRollback = failRollback;
        }

        public bool SupportsAtomicRollback { get; }
        public bool IsOpen { get; private set; }
        public int ExecutionCount { get; private set; }
        public int RollbackCount { get; private set; }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            IsOpen = true;
            try
            {
                return await work(cancellationToken);
            }
            catch (Exception ex)
            {
                RollbackCount++;
                if (_failRollback)
                    throw new AggregateException(
                        "Atomic persistence failed and rollback could not be confirmed.", ex);
                throw;
            }
            finally { IsOpen = false; }
        }
    }
}

file static class PersistenceResultAssertions
{
    public static IEnumerable<IngestionItemStatus> Statuses(this PersistenceResult result) =>
        result.Outcomes.Select(outcome => outcome.Status);
}
