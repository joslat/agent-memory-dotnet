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

public sealed class PersistenceStageFactBatchSemanticsTests
{
    [Fact]
    public async Task PersistAsync_CaseInsensitiveDuplicateFacts_KeepSequentialReadWriteOrder()
    {
        var calls = new List<string>();
        var factRepository = Substitute.For<IFactRepository, IBatchMemoryRepository<Fact>>();
        var batch = (IBatchMemoryRepository<Fact>)factRepository;
        var findCall = 0;
        factRepository.FindByTripleAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MemoryScope?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var subject = call.ArgAt<string>(0);
                calls.Add($"find:{subject}");
                findCall++;
                return findCall == 1
                    ? null
                    : new Fact
                    {
                        FactId = "fact-1",
                        Subject = "Alice",
                        Predicate = "likes",
                        Object = "Coffee",
                        Confidence = 0.9,
                        OwnerId = "owner-1",
                        CreatedAtUtc = DateTimeOffset.Parse("2026-07-29T00:00:00Z")
                    };
            });
        factRepository.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var fact = call.Arg<Fact>();
                calls.Add($"upsert:{fact.Subject}");
                return fact;
            });

        var embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[4]);
        var idGenerator = Substitute.For<IIdGenerator>();
        idGenerator.GenerateId().Returns("fact-1", "fact-2");

        var sut = new PersistenceStage(
            embeddingOrchestrator,
            Substitute.For<IEntityRepository>(),
            factRepository,
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(),
            Substitute.For<IClock>(),
            idGenerator,
            NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions()));

        var extraction = new ExtractionStageResult
        {
            SourceMessageIds = ["message-1"],
            FilteredFacts =
            [
                new ExtractedFact
                {
                    Subject = "Alice",
                    Predicate = "likes",
                    Object = "Coffee",
                    Confidence = 0.9
                },
                new ExtractedFact
                {
                    Subject = "alice",
                    Predicate = "LIKES",
                    Object = "coffee",
                    Confidence = 0.8
                }
            ]
        };

        var result = await sut.PersistAsync(extraction, ownerId: "owner-1");

        calls.Should().Equal("find:Alice", "upsert:Alice", "find:alice", "upsert:Alice");
        await batch.DidNotReceive().UpsertBatchAsync(
            Arg.Any<IReadOnlyList<Fact>>(), Arg.Any<CancellationToken>());
        result.FactCount.Should().Be(2);
    }
}
