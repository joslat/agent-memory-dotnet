using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>Regressions from the review of the profile-tier flip and the indexed candidates (2026-09-26).</summary>
public sealed class ProfileTierReviewTests
{
    // ---- H1: the tier and its schema are one switch ----

    private static Neo4jOptions Neo4jOptionsFor(Action<MemoryOptions>? memory = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(o => memory?.Invoke(o));
        services.AddNeo4jAgentMemory(o =>
        {
            o.Uri = "bolt://localhost:7687";
            o.Username = "neo4j";
            o.Password = "x";
        });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<Neo4jOptions>>().Value;
    }

    [Fact]
    public void A_default_host_activates_the_profile_schema_extension()
    {
        // Without it the :User MERGE has no unique constraint: label scans, and duplicate :User nodes
        // under concurrent first writes for one owner.
        Neo4jOptionsFor().Extensions.Should().Contain("working-memory");
    }

    [Fact]
    public void A_host_that_turns_the_tier_off_does_not_get_its_schema()
    {
        Neo4jOptionsFor(o => o.WorkingMemory.Enabled = false).Extensions.Should().NotContain("working-memory");
    }

    // ---- M1: the block is what answers a question that matched nothing ----

    [Fact]
    public void The_formatter_keeps_the_profile_when_nothing_else_was_recalled()
    {
        var result = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s",
                AssembledAtUtc = DateTimeOffset.UnixEpoch,
                WorkingMemoryBlock = "Alex works at Northwind",
            },
            TotalItemsRetrieved = 0,
        };

        MemoryContextFormatter.FormatRecallResult(result).Should().Contain("Alex works at Northwind");
    }

    // ---- M7 / M8 (dark): the index path gets vectors, and the turn's merged copy ----

    private static Entity Person(string id, string name, float[]? vector, params string[] aliases) => new()
    {
        EntityId = id, Name = name, Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch,
        Embedding = vector, Aliases = aliases,
    };

    private static (CompositeEntityResolver Sut, IEntityRepository Entities) Resolver()
    {
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        entities.GetByTypeWithoutEmbeddingAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new[] { 1f, 0f }));
        var options = new ExtractionOptions();
        options.EntityResolution.IndexedCandidates = true;
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return (new CompositeEntityResolver(entities, embeddings, Options.Create(options), Substitute.For<IClock>(), ids,
            NullLogger<CompositeEntityResolver>.Instance), entities);
    }

    [Fact]
    public async Task Index_hits_without_vectors_are_read_in_full_so_semantic_matching_still_works()
    {
        // A host that omits vectors from recall gets vector-less hits; the semantic matcher skips those.
        var (sut, entities) = Resolver();
        entities.SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity, double)>>([(Person("p1", "Carla Mendes", null), 1.0)]));
        entities.GetByIdAsync("p1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<Entity?>(Person("p1", "Carla Mendes", [1f, 0f])));

        var resolved = await sut.ResolveEntityAsync(new ExtractedEntity { Name = "Carl M.", Type = "PERSON" }, ["m1"]);

        resolved.EntityId.Should().Be("p1");
    }

    [Fact]
    public async Task The_turns_merged_copy_wins_over_the_indexs_stored_copy()
    {
        // This turn already merged "Carla" into p1 (a new alias). The index still returns the stored copy;
        // continuing from it would write p1 back without that alias.
        var (sut, entities) = Resolver();
        entities.GetByTypeWithoutEmbeddingAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([Person("p1", "Carla Mendes", null, "Carla")]));
        entities.GetByIdAsync("p1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(Person("p1", "Carla Mendes", [1f, 0f], "Carla")));
        entities.SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity, double)>>([(Person("p1", "Carla Mendes", [1f, 0f]), 1.0)]));

        var resolved = await sut.ResolveEntityAsync(new ExtractedEntity { Name = "C. Mendes", Type = "PERSON" }, ["m2"]);

        resolved.EntityId.Should().Be("p1");
        resolved.Aliases.Should().Contain("Carla");
    }
}
