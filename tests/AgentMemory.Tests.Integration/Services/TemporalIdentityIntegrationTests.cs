using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>Given extracted date values, verify the actual Core write path and disposable graph.</summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class TemporalIdentityIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jFactRepository _facts;
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IIdGenerator _ids = Substitute.For<IIdGenerator>();
    private static readonly MemoryScope Scope = MemoryScope.For("pablito", includeShared: false);
    private readonly ExtractionOptions _options = new() { CanonicalFactSubjects = true, LinkFactsToEntities = true };

    public TemporalIdentityIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _entities = new(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _facts = new(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0, 0, 0 });
        _ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private CompositeEntityResolver Resolver() => new(_entities, _embeddings, Options.Create(_options),
        TimeProvider.System, _ids, NullLogger<CompositeEntityResolver>.Instance);

    private Task<Entity> Seed(string name, string? owner = "pablito", params string[] aliases) => _entities.UpsertAsync(new Entity
    {
        EntityId = Guid.NewGuid().ToString("N"), Name = name, CanonicalName = name, Type = "OBJECT",
        Confidence = 1, OwnerId = owner, Aliases = aliases, CreatedAtUtc = DateTimeOffset.UtcNow,
    });

    private PersistenceStage Persistence() => new(_embeddings, _entities, _facts,
        Substitute.For<IPreferenceRepository>(), Substitute.For<IRelationshipRepository>(),
        TimeProvider.System, _ids, NullLogger<PersistenceStage>.Instance,
        new PassThroughMemoryPersistenceTransaction(), Options.Create(_options));

    [Theory]
    [InlineData("December 3", "December 12")]
    [InlineData("December 3", "December 4")]
    [InlineData("2026-12-03", "2026-12-12")]
    [InlineData("2026-12-12", "December 12")]
    [InlineData("2027-12-12", "2026-12-12")]
    [InlineData("2026-02-28", "2026-02-29")]
    [InlineData("2026-01-02", "01/02/2026")]
    [InlineData("December 9–12, 2026", "December 9–13, 2026")]
    [InlineData("9–12 diciembre 2026", "9–13 diciembre 2026")]
    [InlineData("2026-12-09/2026-12-12", "2026-12-09/2026-12-13")]
    [InlineData("December 9–13, 2026", "December 9–13")]
    [InlineData("2026-12-12", "2026-12-12/2026-12-12")]
    public async Task Calendar_observations_keep_their_words_and_link_to_their_own_value(string training, string wedding)
    {
        var old = await Seed(training);
        var resolved = await Resolver().ResolveEntityAsync(new ExtractedEntity { Name = wedding, Type = "OBJECT" }, [], Scope);
        resolved.EntityId.Should().NotBe(old.EntityId);
        await Persistence().PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { [wedding] = resolved },
            FilteredFacts = [new ExtractedFact { Subject = "Lucía's wedding", Predicate = "is on", Object = wedding, Confidence = 1 }]
        }, ownerId: Scope.OwnerId);

        var stored = await _facts.FindByTripleAsync("Lucía's wedding", "is on", wedding, Scope);
        stored.Should().NotBeNull();
        stored!.Object.Should().Be(wedding);
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync("MATCH (:Fact {id: $id})-[:ABOUT]->(e:Entity) RETURN e.id AS id", new { id = stored.FactId });
        var links = await cursor.ToListAsync();
        links.Should().ContainSingle();
        ValueExtensions.As<string>(links[0]["id"]).Should().Be(resolved.EntityId);
        (await _entities.GetByIdAsync(old.EntityId))!.Name.Should().Be(training);
    }

    [Theory]
    [InlineData("December 3", "December 12")]
    [InlineData("December 9-12, 2026", "December 9-13, 2026")]
    public Task Surrounding_whitespace_cannot_rewrite_a_date_or_attach_it_to_a_different_value(string stored, string mention) =>
        Calendar_observations_keep_their_words_and_link_to_their_own_value(Padded(stored), Padded(mention));

    [Fact]
    public Task A_padded_complete_value_still_reuses_the_equivalent_calendar_identity() =>
        Equivalent_complete_values_reuse_identity_after_a_new_resolver_is_created("2026-12-12", Padded("12 December 2026"));

    private static string Padded(string text) => new string(' ', 128) + text + new string('\u00a0', 128);

    [Fact]
    public async Task An_incorrect_historical_alias_does_not_change_a_new_observation_or_repair_old_data()
    {
        var old = await Seed("2026-12-03", "pablito", "2026-12-12");
        var result = await Resolver().ResolveEntityAsync(new ExtractedEntity { Name = "2026-12-12", Type = "OBJECT" }, [], Scope);
        result.EntityId.Should().NotBe(old.EntityId);
        result.Name.Should().Be("2026-12-12");
        var retained = await _entities.GetByIdAsync(old.EntityId);
        retained!.Name.Should().Be("2026-12-03");
        retained.Aliases.Should().Equal("2026-12-12");
    }

    [Theory]
    [InlineData("12 December 2026", "2026-12-12")]
    [InlineData("2028-02-29", "29 febrero 2028")]
    [InlineData("December 9–13, 2026", "2026-12-09/2026-12-13")]
    [InlineData("2026-12-30/2027-01-02", "December 30, 2026 through January 2, 2027")]
    public async Task Equivalent_complete_values_reuse_identity_after_a_new_resolver_is_created(string stored, string mention)
    {
        var old = await Seed(stored);
        var first = await Resolver().ResolveEntityAsync(new ExtractedEntity { Name = mention, Type = "OBJECT" }, [], Scope);
        var second = await Resolver().ResolveEntityAsync(new ExtractedEntity { Name = mention, Type = "OBJECT" }, [], Scope);
        first.EntityId.Should().Be(old.EntityId);
        second.EntityId.Should().Be(old.EntityId);
        (await _entities.GetByIdAsync(old.EntityId))!.Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task Equal_calendar_values_do_not_cross_an_owners_private_boundary()
    {
        var foreign = await Seed("2026-12-12", "another-owner");
        var result = await Resolver().ResolveEntityAsync(new ExtractedEntity { Name = "12 December 2026", Type = "OBJECT" }, [], Scope);
        result.EntityId.Should().NotBe(foreign.EntityId);
        result.OwnerId.Should().Be("pablito");
    }

    [Theory]
    [InlineData("2026-12-03", "2026-12-12")]
    [InlineData("December 9–12, 2026", "December 9–13, 2026")]
    public async Task A_historical_alias_copied_into_the_persistence_map_cannot_create_a_wrong_about_edge(string stored, string literal)
    {
        var old = await Seed(stored, "pablito", literal);
        await Persistence().PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity> { [old.Name] = old },
            FilteredFacts = [new ExtractedFact { Subject = "Lucía's wedding", Predicate = "is on", Object = literal, Confidence = 1 }]
        }, ownerId: Scope.OwnerId);
        var fact = await _facts.FindByTripleAsync("Lucía's wedding", "is on", literal, Scope);
        fact.Should().NotBeNull();
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync("MATCH (:Fact {id: $id})-[:ABOUT]->(e:Entity) RETURN e.id AS id", new { id = fact!.FactId });
        (await cursor.ToListAsync()).Should().BeEmpty();
        (await _entities.GetByIdAsync(old.EntityId))!.Aliases.Should().Equal(literal);
    }
}
