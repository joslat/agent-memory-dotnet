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
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// I-2: a fact names its entities by their resolved names, so mentions of one person merge into one fact.
/// Recorded live: "Tomás | moved to | …" stood next to facts about "Tomás Silva", one person, two subjects.
/// </summary>
public sealed class CanonicalFactSubjectTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly List<Fact> _upserted = [];

    private PersistenceStage Sut(bool canonical)
    {
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => { _upserted.Add(call.Arg<Fact>()); return call.Arg<Fact>(); });
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));

        return new PersistenceStage(embeddings, entities, _facts, Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(), clock, ids, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions
            {
                LinkFactsToEntities = true,
                CanonicalFactSubjects = canonical,
                EnableBatchMemoryUpserts = false,
            }));
    }

    // The extraction said "Tomás"; resolution found the known "Tomás Silva".
    private static ExtractionStageResult Extraction() => new()
    {
        SourceMessageIds = ["message-1"],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tomás"] = new() { EntityId = "entity-tomas", Name = "Tomás Silva", Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch },
        },
        FilteredFacts =
        [
            new ExtractedFact { Subject = "Tomás", Predicate = "moved to", Object = "analytics team", Confidence = 0.9 },
            new ExtractedFact { Subject = "user", Predicate = "works with", Object = "Tomás", Confidence = 0.9 },
        ],
    };

    [Fact]
    public async Task On_a_fact_is_stored_under_the_resolved_name_and_keeps_the_words_used()
    {
        await Sut(canonical: true).PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        var moved = _upserted.Single(f => f.Predicate == "moved to");
        moved.Subject.Should().Be("Tomás Silva");
        moved.Metadata["subject_surface"].Should().Be("Tomás");
        var worksWith = _upserted.Single(f => f.Predicate == "works with");
        worksWith.Subject.Should().Be("user", "a name that resolved to no entity is kept as written");
        worksWith.Object.Should().Be("Tomás Silva");
        worksWith.Metadata["object_surface"].Should().Be("Tomás");
        // The resolved name still finds its entity, so the facts keep their ABOUT edges.
        await _facts.Received(2).CreateAboutRelationshipAsync(Arg.Any<string>(), "entity-tomas", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Off_facts_keep_the_words_used()
    {
        await Sut(canonical: false).PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        _upserted.Single(f => f.Predicate == "moved to").Subject.Should().Be("Tomás");
        _upserted.Should().OnlyContain(f => !f.Metadata.ContainsKey("subject_surface") && !f.Metadata.ContainsKey("object_surface"));
    }

    [Theory]
    [InlineData("Tomás", "works at", "Tomás Silva", "Works at")]
    [InlineData("Tomás Silva", "works_at", "Tomás Silva", "works at")]   // review round 3: the storage key folds separators
    public async Task Two_phrasings_that_become_one_fact_are_written_one_after_the_other_on_the_default_batch_path(
        string firstSubject, string firstPredicate, string secondSubject, string secondPredicate)
    {
        // Review round 2: "Tomás | works at | Acme" and "Tomás Silva | Works at | Acme" were distinct as
        // extracted, so they were batched; canonicalized they are one MERGE key, the batch folded them, and
        // the replay upserted both again (mention count 4 for 2 mentions).
        var facts = Substitute.For<IFactRepository, IBatchMemoryRepository<Fact>>();
        facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Fact>());
        var batch = (IBatchMemoryRepository<Fact>)facts;
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var sut = new PersistenceStage(embeddings, entities, facts, Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(), Substitute.For<IClock>(), ids, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { CanonicalFactSubjects = true }));
        var tomas = new Entity { EntityId = "entity-tomas", Name = "Tomás Silva", Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };

        await sut.PersistAsync(new ExtractionStageResult
        {
            SourceMessageIds = ["message-1"],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Tomás"] = tomas, ["Tomás Silva"] = tomas },
            FilteredFacts =
            [
                new ExtractedFact { Subject = firstSubject, Predicate = firstPredicate, Object = "Acme", Confidence = 0.9 },
                new ExtractedFact { Subject = secondSubject, Predicate = secondPredicate, Object = "Acme", Confidence = 0.9 },
            ],
        }, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await batch.DidNotReceiveWithAnyArgs().UpsertBatchAsync(default!, default);
        // Sequential: the second one's pre-fetch runs after the first write, so it can see it.
        Received.InOrder(() =>
        {
            facts.FindByTripleAsync("Tomás Silva", firstPredicate, "Acme", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
            facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
            facts.FindByTripleAsync("Tomás Silva", secondPredicate, "Acme", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
            facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        });
    }
}
