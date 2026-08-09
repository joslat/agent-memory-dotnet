using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class PersistenceStageProvenanceCapabilityTests
{
    [Fact]
    public async Task PersistAsync_RepositoriesPersistProvenanceOnUpsert_DoesNotWriteEdgesTwice()
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);
        var entityRepo = Substitute.For<IEntityRepository, IUpsertPersistsProvenance>();
        var factRepo = Substitute.For<IFactRepository, IUpsertPersistsProvenance>();
        var preferenceRepo = Substitute.For<IPreferenceRepository, IUpsertPersistsProvenance>();
        var relationshipRepo = Substitute.For<IRelationshipRepository>();
        var clock = Substitute.For<IClock>();
        var ids = Substitute.For<IIdGenerator>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        ids.GenerateId().Returns("memory-1");
        entityRepo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());
        factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Fact>());
        preferenceRepo.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Preference>());

        var extraction = new ExtractionStageResult
        {
            SourceMessageIds = ["message-1", "message-2"],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = new Entity
                {
                    EntityId = "entity-1",
                    Name = "Alice",
                    Type = "Person",
                    Confidence = 0.9,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                },
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
                    PreferenceText = "Prefers coffee",
                    Confidence = 0.9,
                },
            ],
        };
        var sut = new PersistenceStage(
            embeddings, entityRepo, factRepo, preferenceRepo, relationshipRepo, clock, ids,
            NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction());

        var result = await sut.PersistAsync(extraction);

        result.EntityCount.Should().Be(1);
        result.FactCount.Should().Be(1);
        result.PreferenceCount.Should().Be(1);
        await entityRepo.DidNotReceive().CreateExtractedFromRelationshipAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await factRepo.DidNotReceive().CreateExtractedFromRelationshipAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await preferenceRepo.DidNotReceive().CreateExtractedFromRelationshipAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
