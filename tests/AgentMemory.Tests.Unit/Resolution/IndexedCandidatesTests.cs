using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// D15: resolution candidates without vectors, and the semantic stage through the vector index. Measured,
/// an owner with 5,000 people spent 0.8–1.2 s per resolution loading every one of them with its vector.
/// </summary>
public sealed class IndexedCandidatesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly List<Entity> _upserted = [];

    private static Entity Person(string id, string name, float[]? vector) => new()
    {
        EntityId = id, Name = name, Type = "PERSON", Confidence = 1, CreatedAtUtc = T0, Embedding = vector,
    };

    public IndexedCandidatesTests()
    {
        _entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _upserted.Add(ci.Arg<Entity>()); return Task.FromResult(ci.Arg<Entity>()); });
        _entities.GetByTypeAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _entities.GetByTypeWithoutEmbeddingAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _entities.SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity, double)>>([]));
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new[] { 1f, 0f }));
    }

    private CompositeEntityResolver Sut(bool indexed)
    {
        var options = new ExtractionOptions();
        options.EntityResolution.IndexedCandidates = indexed;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new CompositeEntityResolver(_entities, _embeddings, Options.Create(options), clock, ids,
            NullLogger<CompositeEntityResolver>.Instance);
    }

    [Fact]
    public async Task Off_the_candidates_are_read_with_their_vectors_as_before()
    {
        await Sut(indexed: false).ResolveEntityAsync(new ExtractedEntity { Name = "Carla", Type = "PERSON" }, []);

        await _entities.Received().GetByTypeAsync("PERSON", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _entities.DidNotReceive().GetByTypeWithoutEmbeddingAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task On_a_string_match_is_read_back_in_full_before_it_is_written()
    {
        // The lean candidate has no vector; auto-merging it as-is would write the entity without one.
        _entities.GetByTypeWithoutEmbeddingAsync("PERSON", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([Person("p1", "Carla Mendes", null)]));
        _entities.GetByIdAsync("p1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(Person("p1", "Carla Mendes", [0.5f, 0.5f])));

        var resolved = await Sut(indexed: true).ResolveEntityAsync(new ExtractedEntity { Name = "carla mendes", Type = "PERSON" }, ["m1"]);

        resolved.EntityId.Should().Be("p1");
        resolved.Embedding.Should().Equal(0.5f, 0.5f);
        await _entities.DidNotReceive().GetByTypeAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        _upserted.Should().OnlyContain(e => e.Embedding != null, "no write may erase a stored vector");
    }

    [Fact]
    public async Task On_the_semantic_stage_matches_through_the_vector_index()
    {
        // No string matcher links "Carl M." to "Carla Mendes"; only the vector can, and it now comes from
        // the index, not from a candidate list that carries every vector.
        var carla = Person("p1", "Carla Mendes", [1f, 0f]);
        _entities.SearchByVectorAsync(Arg.Any<float[]>(), 20, Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity, double)>>([(carla, 1.0)]));

        var resolved = await Sut(indexed: true).ResolveEntityAsync(new ExtractedEntity { Name = "Carl M.", Type = "PERSON" }, ["m1"]);

        resolved.EntityId.Should().Be("p1");
        await _entities.Received().SearchByVectorAsync(Arg.Any<float[]>(), 20, 0.8, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task On_an_index_neighbour_of_another_type_is_not_a_candidate()
    {
        var paris = Person("c1", "Paris", [1f, 0f]) with { Type = "LOCATION" };
        _entities.SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity, double)>>([(paris, 1.0)]));

        var resolved = await Sut(indexed: true).ResolveEntityAsync(new ExtractedEntity { Name = "Paris Hilton", Type = "PERSON" }, ["m1"]);

        resolved.EntityId.Should().NotBe("c1");
    }
}
