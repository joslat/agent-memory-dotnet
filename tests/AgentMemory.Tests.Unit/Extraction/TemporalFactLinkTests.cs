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

public sealed class TemporalFactLinkTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly List<Fact> _written = [];

    private PersistenceStage Persistence()
    {
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => { _written.Add(call.Arg<Fact>());return call.Arg<Fact>(); });
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0, 0, 0 });
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new(embeddings, entities, _facts, Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(), TimeProvider.System, ids,
            NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { LinkFactsToEntities = true, CanonicalFactSubjects = true }));
    }

    private static Entity Existing(string name, params string[] aliases) => new()
    {
        EntityId = "old-date", Name = name, Type = "OBJECT", Aliases = aliases,
        Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    [Theory]
    [InlineData("December 3", "December 12", false)]
    [InlineData("December 3", "December 12", true)]
    [InlineData("2026-12-03", "2026-12-12", false)]
    [InlineData("2026-12-03", "2026-12-12", true)]
    [InlineData("2026-12-12", "December 12", false)]
    [InlineData("December 12", "2026-12-12", false)]
    [InlineData("2026-01-02", "01/02/2026", false)]
    public async Task A_historical_alias_cannot_attach_a_literal_fact_to_an_incompatible_calendar_node(
        string storedName, string literal, bool subject)
    {
        await Persistence().PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity> { [storedName] = Existing(storedName, literal) },
            FilteredFacts = [new ExtractedFact { Subject = subject ? literal : "wedding", Predicate = "is on",
                Object = subject ? "wedding" : literal, Confidence = 1 }],
        }, ownerId: "owner");
        (subject ? _written.Single().Subject : _written.Single().Object).Should().Be(literal);
        await _facts.DidNotReceive().CreateAboutRelationshipAsync(Arg.Any<string>(), "old-date", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_corrupt_external_resolver_map_cannot_rename_a_date_into_a_person()
    {
        var entity = Existing("Alex Rivera") with { Type = "PERSON" };
        await Persistence().PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity> { ["2026-12-12"] = entity },
            FilteredFacts = [new ExtractedFact { Subject = "wedding", Predicate = "is on", Object = "2026-12-12", Confidence = 1 }]
        }, ownerId: "owner");
        _written.Single().Object.Should().Be("2026-12-12");
        await _facts.DidNotReceive().CreateAboutRelationshipAsync(Arg.Any<string>(), "old-date", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("2026-12-12", "12 December 2026")]
    [InlineData("December 12", "December 12")]
    [InlineData("01/02/2026", "01/02/2026")]
    public async Task Compatible_primary_values_keep_their_original_fact_text_and_valid_link(string literal, string target)
    {
        await Persistence().PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity> { [literal] = Existing(target) },
            FilteredFacts = [new ExtractedFact { Subject = "wedding", Predicate = "is on", Object = literal, Confidence = 1 }]
        }, ownerId: "owner");
        _written.Single().Object.Should().Be(literal);
        await _facts.Received(1).CreateAboutRelationshipAsync(Arg.Any<string>(), "old-date", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Contradictory_primary_and_canonical_dates_do_not_authorize_a_link()
    {
        var entity = Existing("2026-12-12") with { CanonicalName = "2026-12-03" };
        await Persistence().PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity> { [entity.Name] = entity },
            FilteredFacts = [new ExtractedFact { Subject = "wedding", Predicate = "is on", Object = entity.Name, Confidence = 1 }]
        }, ownerId: "owner");
        await _facts.DidNotReceive().CreateAboutRelationshipAsync(Arg.Any<string>(), "old-date", Arg.Any<CancellationToken>());
    }
}
