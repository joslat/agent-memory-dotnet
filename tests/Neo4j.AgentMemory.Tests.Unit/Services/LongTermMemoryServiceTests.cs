using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class LongTermMemoryServiceTests
{
    private readonly IEntityRepository _entityRepo;
    private readonly IFactRepository _factRepo;
    private readonly IPreferenceRepository _prefRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

    public LongTermMemoryServiceTests()
    {
        _entityRepo = Substitute.For<IEntityRepository>();
        _factRepo = Substitute.For<IFactRepository>();
        _prefRepo = Substitute.For<IPreferenceRepository>();
        _relRepo = Substitute.For<IRelationshipRepository>();
        _embeddingGenerator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        _embeddingGenerator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IEnumerable<string>>();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>(
                    texts.Select(_ => new Embedding<float>(new float[1536])).ToList());
                return Task.FromResult(embeddings);
            });

        _entityRepo
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Entity>()));

        _factRepo
            .UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Fact>()));

        _prefRepo
            .UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Preference>()));

        _relRepo
            .UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Relationship>()));
    }

    private LongTermMemoryService CreateSut(IOptions<LongTermMemoryOptions>? options = null) =>
        new(_entityRepo, _factRepo, _prefRepo, _relRepo, _embeddingGenerator,
            options ?? Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance);

    // ---- Entity tests ----

    [Fact]
    public async Task AddEntityAsync_GeneratesEmbeddingWhenEnabled()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { GenerateEntityEmbeddings = true }));
        var entity = CreateEntity("e-1", withEmbedding: false);

        await sut.AddEntityAsync(entity);

        await _embeddingGenerator
            .Received(1)
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddEntityAsync_SkipsEmbeddingWhenAlreadyProvided()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { GenerateEntityEmbeddings = true }));
        var entity = CreateEntity("e-1", withEmbedding: true);

        await sut.AddEntityAsync(entity);

        await _embeddingGenerator
            .DidNotReceive()
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddEntityAsync_UpsertsToRepository()
    {
        var sut = CreateSut();
        var entity = CreateEntity("e-1");

        await sut.AddEntityAsync(entity);

        await _entityRepo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEntitiesByNameAsync_DelegatesToRepository()
    {
        _entityRepo
            .GetByNameAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        var sut = CreateSut();

        await sut.GetEntitiesByNameAsync("Alice");

        await _entityRepo
            .Received(1)
            .GetByNameAsync("Alice", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchEntitiesAsync_DelegatesToRepositoryAndStripsScores()
    {
        var entity = CreateEntity("e-1");
        _entityRepo
            .SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity, double)>>(new[] { (entity, 0.9) }));
        var sut = CreateSut();

        var result = await sut.SearchEntitiesAsync(new float[1536]);

        result.Should().ContainSingle();
        result[0].Should().Be(entity);
    }

    // ---- Preference tests ----

    [Fact]
    public async Task AddPreferenceAsync_GeneratesEmbeddingWhenEnabled()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { GeneratePreferenceEmbeddings = true }));
        var pref = CreatePreference("p-1", withEmbedding: false);

        await sut.AddPreferenceAsync(pref);

        await _embeddingGenerator
            .Received(1)
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddPreferenceAsync_UpsertsToRepository()
    {
        var sut = CreateSut();
        var pref = CreatePreference("p-1");

        await sut.AddPreferenceAsync(pref);

        await _prefRepo.Received(1).UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPreferencesByCategoryAsync_DelegatesToRepository()
    {
        _prefRepo
            .GetByCategoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        var sut = CreateSut();

        await sut.GetPreferencesByCategoryAsync("style");

        await _prefRepo
            .Received(1)
            .GetByCategoryAsync("style", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchPreferencesAsync_StripsScores()
    {
        var pref = CreatePreference("p-1");
        _prefRepo
            .SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Preference, double)>>(new[] { (pref, 0.85) }));
        var sut = CreateSut();

        var result = await sut.SearchPreferencesAsync(new float[1536]);

        result.Should().ContainSingle();
        result[0].Should().Be(pref);
    }

    // ---- Fact tests ----

    [Fact]
    public async Task AddFactAsync_GeneratesEmbedding()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { GenerateFactEmbeddings = true }));
        var fact = CreateFact("f-1", withEmbedding: false);

        await sut.AddFactAsync(fact);

        await _embeddingGenerator
            .Received(1)
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFactsBySubjectAsync_DelegatesToRepository()
    {
        _factRepo
            .GetBySubjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        var sut = CreateSut();

        await sut.GetFactsBySubjectAsync("Alice");

        await _factRepo
            .Received(1)
            .GetBySubjectAsync("Alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchFactsAsync_StripsScores()
    {
        var fact = CreateFact("f-1");
        _factRepo
            .SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Fact, double)>>(new[] { (fact, 0.88) }));
        var sut = CreateSut();

        var result = await sut.SearchFactsAsync(new float[1536]);

        result.Should().ContainSingle();
        result[0].Should().Be(fact);
    }

    // ---- Relationship tests ----

    [Fact]
    public async Task AddRelationshipAsync_UpsertsToRepository()
    {
        var sut = CreateSut();
        var rel = CreateRelationship("r-1");

        await sut.AddRelationshipAsync(rel);

        await _relRepo.Received(1).UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEntityRelationshipsAsync_DelegatesToRepository()
    {
        _relRepo
            .GetByEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(Array.Empty<Relationship>()));
        var sut = CreateSut();

        await sut.GetEntityRelationshipsAsync("e-1");

        await _relRepo
            .Received(1)
            .GetByEntityAsync("e-1", Arg.Any<CancellationToken>());
    }

    // ---- Helpers ----

    private static Entity CreateEntity(string id, bool withEmbedding = false) => new()
    {
        EntityId = id,
        Name = "Alice",
        Type = "Person",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Embedding = withEmbedding ? new float[1536] : null
    };

    private static Fact CreateFact(string id, bool withEmbedding = false) => new()
    {
        FactId = id,
        Subject = "Alice",
        Predicate = "works_at",
        Object = "Acme Corp",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Embedding = withEmbedding ? new float[1536] : null
    };

    private static Preference CreatePreference(string id, bool withEmbedding = false) => new()
    {
        PreferenceId = id,
        Category = "style",
        PreferenceText = "Prefers concise answers",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Embedding = withEmbedding ? new float[1536] : null
    };

    private static Relationship CreateRelationship(string id) => new()
    {
        RelationshipId = id,
        SourceEntityId = "e-1",
        TargetEntityId = "e-2",
        RelationshipType = "KNOWS",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    // ── DeletePreferenceAsync tests ──

    [Fact]
    public async Task DeletePreferenceAsync_DelegatesToRepositoryWithCorrectId()
    {
        var sut = CreateSut();
        _prefRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await sut.DeletePreferenceAsync("pref-123");

        await _prefRepo.Received(1).DeleteAsync("pref-123", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePreferenceAsync_DelegatesToRepositoryWithAnyId()
    {
        var sut = CreateSut();
        _prefRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await sut.DeletePreferenceAsync("any-id-value");

        await _prefRepo.Received(1).DeleteAsync("any-id-value", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePreferenceAsync_RepositoryIsCalled()
    {
        var sut = CreateSut();
        _prefRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await sut.DeletePreferenceAsync("pref-to-delete");

        await _prefRepo.Received(1).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
