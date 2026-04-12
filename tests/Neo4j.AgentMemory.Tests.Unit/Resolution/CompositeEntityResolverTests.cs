using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Resolution;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Resolution;

public sealed class CompositeEntityResolverTests
{
    private static readonly DateTimeOffset FixedTime = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private const string NewEntityId = "new-entity-id";

    private readonly IEntityRepository _entityRepo;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;

    public CompositeEntityResolverTests()
    {
        _entityRepo = Substitute.For<IEntityRepository>();
        _embeddingProvider = Substitute.For<IEmbeddingProvider>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _clock.UtcNow.Returns(FixedTime);
        _idGenerator.GenerateId().Returns(NewEntityId);

        // Default: zero vector (orthogonal to any unit vector, no semantic match above threshold)
        _embeddingProvider
            .GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[4]));

        _entityRepo
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Entity>()));
    }

    private CompositeEntityResolver CreateSut(ExtractionOptions? options = null)
    {
        var opts = Options.Create(options ?? new ExtractionOptions());
        return new CompositeEntityResolver(
            _entityRepo,
            _embeddingProvider,
            opts,
            _clock,
            _idGenerator,
            NullLogger<CompositeEntityResolver>.Instance);
    }

    private static Entity MakeEntity(
        string id,
        string name,
        string type = "Person",
        float[]? embedding = null,
        params string[] aliases) =>
        new()
        {
            EntityId = id,
            Name = name,
            Type = type,
            Confidence = 1.0,
            Embedding = embedding,
            Aliases = aliases,
            CreatedAtUtc = FixedTime
        };

    private static ExtractedEntity MakeCandidate(string name, string type = "Person") =>
        new() { Name = name, Type = type };

    [Fact]
    public async Task ResolveEntityAsync_ExactMatch_ReturnsExisting_WithoutCallingEmbeddingProvider()
    {
        var existing = new[] { MakeEntity("e1", "Alice") };
        _entityRepo.GetByTypeAsync("Person", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(existing));

        var sut = CreateSut();
        var result = await sut.ResolveEntityAsync(MakeCandidate("Alice"), Array.Empty<string>());

        result.EntityId.Should().Be("e1");
        // Exact match short-circuits — embedding provider not called
        await _embeddingProvider.DidNotReceive()
            .GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEntityAsync_FuzzyMatchWhenExactFails_ReturnsExisting()
    {
        var existing = new[] { MakeEntity("e1", "John Smith") };
        _entityRepo.GetByTypeAsync("Person", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(existing));

        var opts = new ExtractionOptions
        {
            EntityResolution = new EntityResolutionOptions
            {
                FuzzyMatchThreshold = 0.5,
                EnableSemanticMatch = false
            },
            SameAsThreshold = 0.4,
            AutoMergeThreshold = 0.95
        };

        var sut = CreateSut(opts);
        var result = await sut.ResolveEntityAsync(MakeCandidate("Smith John"), Array.Empty<string>());

        result.EntityId.Should().Be("e1");
    }

    [Fact]
    public async Task ResolveEntityAsync_SemanticMatchWhenFuzzyFails_ReturnsExisting()
    {
        var unitVec = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var existing = new[] { MakeEntity("e1", "Completely Different Name", embedding: unitVec) };
        _entityRepo.GetByTypeAsync("Person", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(existing));

        _embeddingProvider
            .GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(unitVec));

        var opts = new ExtractionOptions
        {
            EntityResolution = new EntityResolutionOptions
            {
                EnableFuzzyMatch = false,
                SemanticMatchThreshold = 0.9
            },
            SameAsThreshold = 0.5,
            AutoMergeThreshold = 0.99
        };

        var sut = CreateSut(opts);
        var result = await sut.ResolveEntityAsync(MakeCandidate("Alice"), Array.Empty<string>());

        result.EntityId.Should().Be("e1");
    }

    [Fact]
    public async Task ResolveEntityAsync_NoMatch_CreatesNewEntity()
    {
        _entityRepo.GetByTypeAsync("Person", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));

        var sut = CreateSut();
        var result = await sut.ResolveEntityAsync(MakeCandidate("Alice"), new[] { "msg1" });

        result.EntityId.Should().Be(NewEntityId);
        result.Name.Should().Be("Alice");
        result.SourceMessageIds.Should().Contain("msg1");
        await _entityRepo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEntityAsync_TypeStrictFiltering_FetchesCandidatesOfCorrectType()
    {
        _entityRepo.GetByTypeAsync("Organization", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));

        var sut = CreateSut();
        await sut.ResolveEntityAsync(MakeCandidate("OpenAI", type: "Organization"), Array.Empty<string>());

        await _entityRepo.Received(1).GetByTypeAsync("Organization", Arg.Any<CancellationToken>());
        await _entityRepo.DidNotReceive().GetByTypeAsync("Person", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEntityAsync_ExactMatchAboveAutoMergeThreshold_CallsUpsert()
    {
        // Exact match → confidence = 1.0 >= AutoMergeThreshold (0.95) → auto-merge
        var existing = new[] { MakeEntity("e1", "Alice Smith", aliases: "Ally Smith") };
        _entityRepo.GetByTypeAsync("Person", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(existing));

        var sut = CreateSut(new ExtractionOptions
        {
            AutoMergeThreshold = 0.95,
            SameAsThreshold = 0.85
        });

        await sut.ResolveEntityAsync(MakeCandidate("Ally Smith"), Array.Empty<string>());

        await _entityRepo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveEntityAsync_ConfidenceInSameAsRange_ReturnsExistingWithoutUpsert()
    {
        // Fuzzy score for "John Smith Jr" vs "John Smith" should be in SameAs range
        var existing = new[] { MakeEntity("e1", "John Smith") };
        _entityRepo.GetByTypeAsync("Person", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(existing));

        var opts = new ExtractionOptions
        {
            EntityResolution = new EntityResolutionOptions
            {
                EnableExactMatch = false,
                FuzzyMatchThreshold = 0.6,
                EnableSemanticMatch = false
            },
            SameAsThreshold = 0.6,
            AutoMergeThreshold = 0.99 // high enough to never trigger auto-merge
        };

        var sut = CreateSut(opts);
        var result = await sut.ResolveEntityAsync(MakeCandidate("John Smith Jr"), Array.Empty<string>());

        result.EntityId.Should().Be("e1");
        // No upsert — only return existing
        await _entityRepo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindPotentialDuplicatesAsync_ReturnsMatchedEntities()
    {
        var existing = new[] { MakeEntity("e1", "Alice") };
        _entityRepo.GetByTypeAsync("Person", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(existing));

        var sut = CreateSut();
        var duplicates = await sut.FindPotentialDuplicatesAsync("Alice", "Person");

        duplicates.Should().HaveCount(1);
        duplicates[0].EntityId.Should().Be("e1");
    }

    [Fact]
    public async Task FindPotentialDuplicatesAsync_NoMatches_ReturnsEmpty()
    {
        _entityRepo.GetByTypeAsync("Person", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));

        var sut = CreateSut();
        var duplicates = await sut.FindPotentialDuplicatesAsync("Unknown Person", "Person");

        duplicates.Should().BeEmpty();
    }
}
