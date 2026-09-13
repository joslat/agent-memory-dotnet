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
/// W-E1: extraction finally links the facts it writes to the entities it writes.
/// </summary>
/// <remarks>
/// <para>
/// <c>CreateAboutRelationshipAsync</c> is public, unit-tested, and proven against live Neo4j — and
/// <b>no ingestion path has ever called it</b>, which this repository states in two comments of its
/// own. Every store probe run by this project reports <c>0 entity(ies)</c>, on every line, across
/// every vertical.
/// </para>
/// <para>
/// The measured consequence is a gradient across three verticals whose shapes all need one
/// operation — resolve a referent to an entity: semantic <c>co-reference</c> 11/15, episodic
/// <c>participant-attribution</c> 4/15, conjunction <c>alias-then-count</c> <b>0/15</b>. The last
/// is immune to more evidence; three arms at 4.7× the facts moved it by one question.
/// </para>
/// <para>
/// <b>These tests pin the limit as hard as the feature.</b> Matching is by NAME, so aliases are not
/// resolved — and a test asserts that explicitly, so nobody reads this as alias resolution and
/// builds on a promise it does not make.
/// </para>
/// </remarks>
public sealed class FactEntityLinkingTests
{
    [Fact]
    public async Task OffByDefault_NoAboutEdgeIsWritten()
    {
        var (sut, factRepository) = Create(linkFactsToEntities: false);

        await sut.PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await factRepository.DidNotReceive().CreateAboutRelationshipAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_LinksAFactToTheEntityItsSubjectNames()
    {
        var (sut, factRepository) = Create(linkFactsToEntities: true);

        await sut.PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await factRepository.Received().CreateAboutRelationshipAsync(
            Arg.Any<string>(), "entity-alice", Arg.Any<CancellationToken>());
    }

    /// <summary>Both ends are linked: the object names an entity too.</summary>
    [Fact]
    public async Task Enabled_LinksTheObjectEndAsWell()
    {
        var (sut, factRepository) = Create(linkFactsToEntities: true);

        await sut.PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await factRepository.Received().CreateAboutRelationshipAsync(
            Arg.Any<string>(), "entity-acme", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A name that matches no entity is skipped silently — not an error, and not a stub entity.
    /// </summary>
    /// <remarks>
    /// Manufacturing an entity for every unmatched string would fill the graph with one node per
    /// noun phrase, which is the opposite of the identity this edge is supposed to carry.
    /// </remarks>
    [Fact]
    public async Task AnUnmatchedNameLinksToNothing()
    {
        var (sut, factRepository) = Create(linkFactsToEntities: true);

        await sut.PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        // "coffee" is an object with no entity of that name.
        await factRepository.DidNotReceive().CreateAboutRelationshipAsync(
            Arg.Any<string>(), "coffee", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// THE LIMIT, pinned: this links by NAME and does NOT resolve aliases.
    /// </summary>
    /// <remarks>
    /// "head office" and "the Calderwick office" are one place in the corpus and two names here.
    /// This option is the substrate alias resolution needs, not alias resolution — and a codebase
    /// that has twice paid for a half-wired feature should say so in a test, not only in prose.
    /// </remarks>
    [Fact]
    public async Task AnAliasIsNotResolved_AndThatIsTheDocumentedLimit()
    {
        var (sut, factRepository) = Create(linkFactsToEntities: true);

        // The entity is stored as "Acme Corp"; the fact says "the Acme office". One place, two names.
        var extraction = Extraction(objectName: "the Acme office");
        await sut.PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await factRepository.DidNotReceive().CreateAboutRelationshipAsync(
            Arg.Any<string>(), "entity-acme", Arg.Any<CancellationToken>());
    }

    /// <summary>A failed edge must not fail the fact that was already persisted.</summary>
    /// <remarks>
    /// The edge enriches a write that has already succeeded. Letting it throw would lose a stored
    /// fact to a failed enrichment — trading a retrieval degradation for data loss.
    /// </remarks>
    [Fact]
    public async Task AFailedLinkDoesNotFailThePersist()
    {
        var (sut, factRepository) = Create(linkFactsToEntities: true);
        factRepository.CreateAboutRelationshipAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("edge write failed")));

        var act = async () => await sut.PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static ExtractionStageResult Extraction(string objectName = "Acme Corp") => new()
    {
        SourceMessageIds = ["message-1"],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alice"] = Entity("entity-alice", "Alice"),
            ["Acme Corp"] = Entity("entity-acme", "Acme Corp"),
        },
        FilteredFacts =
        [
            new ExtractedFact
            {
                Subject = "Alice", Predicate = "works at", Object = objectName, Confidence = 0.9,
            },
            new ExtractedFact
            {
                Subject = "Alice", Predicate = "likes", Object = "coffee", Confidence = 0.9,
            },
        ],
    };

    private static (PersistenceStage Sut, IFactRepository FactRepository) Create(bool linkFactsToEntities)
    {
        var entityRepository = Substitute.For<IEntityRepository>();
        var factRepository = Substitute.For<IFactRepository>();
        var preferenceRepository = Substitute.For<IPreferenceRepository>();
        var relationshipRepository = Substitute.For<IRelationshipRepository>();
        var embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        var clock = Substitute.For<IClock>();
        var idGenerator = Substitute.For<IIdGenerator>();

        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-09-13T00:00:00Z"));
        idGenerator.GenerateId().Returns("fact-1", "fact-2", "fact-3");
        embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[4]);

        // Upsert echoes its input, so the persisted fact carries the subject/object under test.
        entityRepository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());
        factRepository.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Fact>());

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
            Options.Create(new ExtractionOptions
            {
                LinkFactsToEntities = linkFactsToEntities,
                EnableBatchMemoryUpserts = false,
            }));

        return (sut, factRepository);
    }

    private static Entity Entity(string id, string name) => new()
    {
        EntityId = id,
        Name = name,
        Type = "Organisation",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.Parse("2026-09-13T00:00:00Z"),
    };
}
