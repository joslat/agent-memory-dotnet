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
/// USER-RELATION-DROP (22 Nov 2026; the owner: fix it by default, it is data loss). "Theo is my brother" from an owner
/// who never said their name was dropped: a relationship to "user" anchors on the owner's named person, and with no
/// name there is none, so the edge was skipped ("Unknown name or entity: skipped as before"). The speech benchmark lost
/// a two-fact answer this way. Until a name is known, such a relationship is kept as a fact ("Theo | brother of |
/// user"), which recall's connections already follow.
/// </summary>
public sealed class UserRelationKeptAsFactTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IRelationshipRepository _relationships = Substitute.For<IRelationshipRepository>();
    private readonly List<Fact> _upserted = [];

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-22T00:00:00Z");
    private static readonly Entity Theo = new() { EntityId = "entity-theo", Name = "Theo", Type = "PERSON", OwnerId = "owner-1", Confidence = 0.9, CreatedAtUtc = At };
    private static readonly Entity Acme = new() { EntityId = "entity-acme", Name = "Acme", Type = "ORGANIZATION", OwnerId = "owner-1", Confidence = 0.9, CreatedAtUtc = At };
    private static readonly Entity Dana = new() { EntityId = "entity-dana", Name = "Dana", Type = "PERSON", OwnerId = "owner-1", Confidence = 0.9, CreatedAtUtc = At };

    private PersistenceStage Sut(bool resolve = true)
    {
        _entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        _entities.GetByNameAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => { _upserted.Add(call.Arg<Fact>()); return call.Arg<Fact>(); });
        _facts.FindLatestObjectAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Relationship>());
        _relationships.GetBySourceEntityAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>([]));
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(DateTimeOffset.Parse("2026-11-22T00:00:00Z"));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));

        return new PersistenceStage(embeddings, _entities, _facts, Substitute.For<IPreferenceRepository>(),
            _relationships, clock, ids, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions
            {
                ResolveUserToName = resolve,
                LinkFactsToEntities = true,
                EnableBatchMemoryUpserts = false,
            }));
    }

    private static ExtractedRelationship R(string source, string type, string target) =>
        new() { SourceEntity = source, RelationshipType = type, TargetEntity = target, Confidence = 0.8 };

    private static ExtractedFact F(string subject, string predicate, string @object) =>
        new() { Subject = subject, Predicate = predicate, Object = @object, Confidence = 0.9 };

    private static ExtractionStageResult Extraction(ExtractedRelationship[] relationships, params ExtractedFact[] facts) => new()
    {
        SourceMessageIds = ["message-1"],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Theo"] = Theo, ["Acme"] = Acme },
        FilteredRelationships = relationships,
        FilteredFacts = facts,
    };

    private void OwnerIsNamedDana()
    {
        _facts.FindLatestObjectAsync(
                Arg.Is<IReadOnlyCollection<string>>(s => s.Contains("user")),
                Arg.Is<IReadOnlyCollection<string>>(p => p.Contains("is named")),
                Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("Dana"));
        _entities.FindLiveByNameAsync("Dana", "PERSON", Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(Dana));
    }

    [Fact]
    public async Task An_unnamed_owners_brother_is_kept_as_a_fact()
    {
        await Sut().PersistAsync(Extraction([R("Theo", "BROTHER_OF", "user")]), ownerId: "owner-1",
            cancellationToken: CancellationToken.None);

        _upserted.Should().ContainSingle(f => f.Subject == "Theo" && f.Predicate == "brother of" && f.Object == "user",
            "with no name to anchor on, the relationship would otherwise be lost");
        await _relationships.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [Fact]
    public async Task The_owner_as_the_source_reads_the_other_way()
    {
        await Sut().PersistAsync(Extraction([R("user", "WORKS_AT", "Acme")]), ownerId: "owner-1",
            cancellationToken: CancellationToken.None);

        _upserted.Should().ContainSingle(f => f.Subject == "user" && f.Predicate == "works at" && f.Object == "Acme");
    }

    [Fact]
    public async Task Without_name_resolution_it_is_kept_too_and_nothing_is_looked_up()
    {
        await Sut(resolve: false).PersistAsync(Extraction([R("Theo", "BROTHER_OF", "user")]), ownerId: "owner-1",
            cancellationToken: CancellationToken.None);

        _upserted.Should().ContainSingle(f => f.Subject == "Theo" && f.Predicate == "brother of" && f.Object == "user");
        await _facts.DidNotReceiveWithAnyArgs().FindLatestObjectAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_named_owner_keeps_the_relationship_edge()
    {
        var sut = Sut();
        OwnerIsNamedDana();

        await sut.PersistAsync(Extraction([R("Theo", "BROTHER_OF", "user")]), ownerId: "owner-1",
            cancellationToken: CancellationToken.None);

        await _relationships.Received(1).UpsertAsync(
            Arg.Is<Relationship>(r => r.TargetEntityId == "entity-dana" && r.RelationshipType == "BROTHER_OF"),
            Arg.Any<CancellationToken>());
        _upserted.Should().NotContain(f => f.Predicate == "brother of");
    }

    [Fact]
    public async Task A_relation_the_turn_also_stated_as_a_fact_is_stored_once()
    {
        await Sut().PersistAsync(Extraction([R("Theo", "BROTHER_OF", "user")], F("Theo", "brother of", "user")),
            ownerId: "owner-1", cancellationToken: CancellationToken.None);

        _upserted.Where(f => f.Predicate == "brother of").Should().ContainSingle();
    }

    [Fact]
    public async Task A_relationship_between_two_others_is_untouched()
    {
        await Sut().PersistAsync(Extraction([R("Theo", "WORKS_AT", "Acme")]), ownerId: "owner-1",
            cancellationToken: CancellationToken.None);

        _upserted.Should().BeEmpty();
        await _relationships.Received(1).UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Recall_follows_the_kept_fact_to_the_brother()
    {
        var kept = new Fact { FactId = "f1", Subject = "Theo", Predicate = "brother of", Object = "user", OwnerId = "owner-1", Confidence = 0.9, CreatedAtUtc = At };

        MemoryContextAssembler.Connections([kept], new HashSet<string>(["brother"])).Should().Equal("Theo");
    }
}
