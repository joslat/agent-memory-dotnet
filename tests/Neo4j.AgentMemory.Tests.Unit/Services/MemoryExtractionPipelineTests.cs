using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class MemoryExtractionPipelineTests
{
    // ---- Shared mocks ----
    private readonly IEntityExtractor _entityExtractor = Substitute.For<IEntityExtractor>();
    private readonly IFactExtractor _factExtractor = Substitute.For<IFactExtractor>();
    private readonly IPreferenceExtractor _preferenceExtractor = Substitute.For<IPreferenceExtractor>();
    private readonly IRelationshipExtractor _relationshipExtractor = Substitute.For<IRelationshipExtractor>();
    private readonly IEntityResolver _entityResolver = Substitute.For<IEntityResolver>();
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IEntityRepository _entityRepo = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _factRepo = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _prefRepo = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    public MemoryExtractionPipelineTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _embeddingOrchestrator
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[384]));
        _embeddingOrchestrator
            .EmbedFactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[384]));
        _embeddingOrchestrator
            .EmbedPreferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[384]));
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

        // Default extractors return empty
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(Array.Empty<ExtractedEntity>()));
        _factExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedFact>>(Array.Empty<ExtractedFact>()));
        _preferenceExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedPreference>>(Array.Empty<ExtractedPreference>()));
        _relationshipExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedRelationship>>(Array.Empty<ExtractedRelationship>()));
    }

    private MemoryExtractionPipeline CreateSut(ExtractionOptions? opts = null) =>
        new(
            _entityExtractor,
            _factExtractor,
            _preferenceExtractor,
            _relationshipExtractor,
            _entityResolver,
            _embeddingOrchestrator,
            _entityRepo,
            _factRepo,
            _prefRepo,
            _relRepo,
            Options.Create(opts ?? new ExtractionOptions()),
            _clock,
            _idGen,
            NullLogger<MemoryExtractionPipeline>.Instance);

    // ---- Helpers ----

    private static ExtractionRequest MakeRequest(ExtractionTypes types = ExtractionTypes.All) =>
        new()
        {
            Messages = new[] { MakeMessage("msg-1"), MakeMessage("msg-2") },
            SessionId = "session-42",
            TypesToExtract = types
        };

    private static Message MakeMessage(string id) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "session-42",
        Role = "user",
        Content = "Hello world",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static ExtractedEntity MakeExtractedEntity(
        string name = "Alice",
        double confidence = 0.9) =>
        new() { Name = name, Type = "Person", Confidence = confidence };

    private static Entity MakeResolvedEntity(string id, string name = "Alice") =>
        new()
        {
            EntityId = id,
            Name = name,
            Type = "Person",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

    // ---- Tests ----

    [Fact]
    public async Task ExtractAsync_WithEntities_ExtractsValidatesResolvesAndPersists()
    {
        var entity1 = MakeExtractedEntity("Alice");
        var entity2 = MakeExtractedEntity("Bob");
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(new[] { entity1, entity2 }));
        _entityResolver
            .ResolveEntityAsync(Arg.Is<ExtractedEntity>(e => e.Name == "Alice"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-alice", "Alice")));
        _entityResolver
            .ResolveEntityAsync(Arg.Is<ExtractedEntity>(e => e.Name == "Bob"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-bob", "Bob")));

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest());

        await _entityRepo.Received(2).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        result.Entities.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractAsync_FiltersLowConfidenceEntities()
    {
        var lowConf = MakeExtractedEntity("Alice", confidence: 0.3);
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(new[] { lowConf }));

        var sut = CreateSut(new ExtractionOptions { MinConfidenceThreshold = 0.5 });
        await sut.ExtractAsync(MakeRequest());

        await _entityRepo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_FiltersInvalidEntityNames()
    {
        // "the" is a stopword and should fail EntityValidator
        var stopword = MakeExtractedEntity("the", confidence: 0.9);
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(new[] { stopword }));

        var sut = CreateSut();
        await sut.ExtractAsync(MakeRequest());

        await _entityResolver.DidNotReceive()
            .ResolveEntityAsync(Arg.Any<ExtractedEntity>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _entityRepo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_WithFacts_PersistsToRepository()
    {
        var fact = new ExtractedFact
        {
            Subject = "Alice",
            Predicate = "works_at",
            Object = "Acme Corp",
            Confidence = 0.85
        };
        _factExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedFact>>(new[] { fact }));
        _idGen.GenerateId().Returns("fact-id-1");

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest());

        await _factRepo.Received(1).UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        result.Facts.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExtractAsync_WithPreferences_PersistsToRepository()
    {
        var pref = new ExtractedPreference
        {
            Category = "style",
            PreferenceText = "prefers dark mode",
            Confidence = 0.9
        };
        _preferenceExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedPreference>>(new[] { pref }));
        _idGen.GenerateId().Returns("pref-id-1");

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest());

        await _prefRepo.Received(1).UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        result.Preferences.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExtractAsync_WithRelationships_ResolvesEntityIdsAndPersists()
    {
        var extractedAlice = MakeExtractedEntity("Alice");
        var extractedAcme = MakeExtractedEntity("Acme");
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(new[] { extractedAlice, extractedAcme }));
        _entityResolver
            .ResolveEntityAsync(Arg.Is<ExtractedEntity>(e => e.Name == "Alice"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-alice", "Alice")));
        _entityResolver
            .ResolveEntityAsync(Arg.Is<ExtractedEntity>(e => e.Name == "Acme"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-acme", "Acme")));

        var rel = new ExtractedRelationship
        {
            SourceEntity = "Alice",
            TargetEntity = "Acme",
            RelationshipType = "WORKS_FOR",
            Confidence = 0.9
        };
        _relationshipExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedRelationship>>(new[] { rel }));
        _idGen.GenerateId().Returns("rel-id-1");

        var sut = CreateSut();
        await sut.ExtractAsync(MakeRequest());

        await _relRepo.Received(1).UpsertAsync(
            Arg.Is<Relationship>(r =>
                r.SourceEntityId == "e-alice" &&
                r.TargetEntityId == "e-acme" &&
                r.RelationshipType == "WORKS_FOR"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_SkipsRelationshipsWithUnknownEntities()
    {
        // No entities extracted — relationship references non-existent entities
        var rel = new ExtractedRelationship
        {
            SourceEntity = "Ghost",
            TargetEntity = "Nobody",
            RelationshipType = "KNOWS",
            Confidence = 0.9
        };
        _relationshipExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedRelationship>>(new[] { rel }));

        var sut = CreateSut();
        await sut.ExtractAsync(MakeRequest());

        await _relRepo.DidNotReceive().UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_RespectsExtractionTypeFlags()
    {
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(Array.Empty<ExtractedEntity>()));

        var sut = CreateSut();
        await sut.ExtractAsync(MakeRequest(ExtractionTypes.Entities));

        await _factExtractor.DidNotReceive()
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await _preferenceExtractor.DidNotReceive()
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await _relationshipExtractor.DidNotReceive()
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_GeneratesEmbeddings()
    {
        var entity = MakeExtractedEntity("Alice");
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(new[] { entity }));
        _entityResolver
            .ResolveEntityAsync(Arg.Any<ExtractedEntity>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-alice", "Alice"))); // no embedding

        var fact = new ExtractedFact
        {
            Subject = "Alice", Predicate = "is", Object = "a person", Confidence = 0.9
        };
        _factExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedFact>>(new[] { fact }));
        _idGen.GenerateId().Returns("id-1", "id-2");

        var pref = new ExtractedPreference
        {
            Category = "ui", PreferenceText = "dark mode", Confidence = 0.9
        };
        _preferenceExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedPreference>>(new[] { pref }));

        var sut = CreateSut();
        await sut.ExtractAsync(MakeRequest());

        // Entity embedding + fact embedding + preference embedding = 3 calls minimum
        await _embeddingOrchestrator.Received(1)
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _embeddingOrchestrator.Received(1)
            .EmbedFactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _embeddingOrchestrator.Received(1)
            .EmbedPreferenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_EmptyMessages_ReturnsEmptyResult()
    {
        var request = new ExtractionRequest
        {
            Messages = Array.Empty<Message>(),
            SessionId = "session-empty"
        };

        var sut = CreateSut();
        var result = await sut.ExtractAsync(request);

        result.Entities.Should().BeEmpty();
        result.Facts.Should().BeEmpty();
        result.Preferences.Should().BeEmpty();
        result.Relationships.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_ExtractionError_ContinuesGracefully()
    {
        // Entity extractor throws; fact extractor returns data
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var fact = new ExtractedFact
        {
            Subject = "Alice", Predicate = "likes", Object = "coffee", Confidence = 0.9
        };
        _factExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedFact>>(new[] { fact }));
        _idGen.GenerateId().Returns("fact-id-1");

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest());

        // Facts still processed despite entity extractor failure
        await _factRepo.Received(1).UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        result.Facts.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExtractAsync_PersistenceError_ContinuesGracefully()
    {
        var entity1 = MakeExtractedEntity("Alice");
        var entity2 = MakeExtractedEntity("Bob");
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(new[] { entity1, entity2 }));
        _entityResolver
            .ResolveEntityAsync(Arg.Is<ExtractedEntity>(e => e.Name == "Alice"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-alice", "Alice")));
        _entityResolver
            .ResolveEntityAsync(Arg.Is<ExtractedEntity>(e => e.Name == "Bob"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-bob", "Bob")));

        // First call fails, second succeeds
        var calls = 0;
        _entityRepo
            .UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++calls == 1)
                    return Task.FromException<Entity>(new InvalidOperationException("DB error"));
                return Task.FromResult(_.Arg<Entity>());
            });

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest());

        // Both were attempted despite the first failure
        await _entityRepo.Received(2).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // ── EXTRACTED_FROM relationship tests ──

    [Fact]
    public async Task ExtractAsync_Entity_CreatesExtractedFromRelationshipForEachSourceMessage()
    {
        var entity = MakeExtractedEntity("Alice");
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(new[] { entity }));
        _entityResolver
            .ResolveEntityAsync(Arg.Any<ExtractedEntity>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-alice", "Alice")));

        var sut = CreateSut();
        var request = MakeRequest(ExtractionTypes.Entities);

        await sut.ExtractAsync(request);

        // MakeRequest creates 2 messages (msg-1, msg-2)
        await _entityRepo.Received(request.Messages.Count).CreateExtractedFromRelationshipAsync(
            "e-alice", Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_Fact_CreatesExtractedFromRelationshipForEachSourceMessage()
    {
        var fact = new ExtractedFact
        {
            Subject = "Alice", Predicate = "works_at", Object = "Acme",
            Confidence = 0.9
        };
        _factExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedFact>>(new[] { fact }));
        _idGen.GenerateId().Returns("fact-rel-1");

        var sut = CreateSut();
        var request = MakeRequest(ExtractionTypes.Facts);

        await sut.ExtractAsync(request);

        await _factRepo.Received(request.Messages.Count).CreateExtractedFromRelationshipAsync(
            "fact-rel-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_Preference_CreatesExtractedFromRelationshipForEachSourceMessage()
    {
        var pref = new ExtractedPreference
        {
            Category = "style", PreferenceText = "dark mode",
            Confidence = 0.9
        };
        _preferenceExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedPreference>>(new[] { pref }));
        _idGen.GenerateId().Returns("pref-rel-1");

        var sut = CreateSut();
        var request = MakeRequest(ExtractionTypes.Preferences);

        await sut.ExtractAsync(request);

        await _prefRepo.Received(request.Messages.Count).CreateExtractedFromRelationshipAsync(
            "pref-rel-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_ExtractedFromFailure_DoesNotFailExtraction()
    {
        var entity = MakeExtractedEntity("Bob");
        _entityExtractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(new[] { entity }));
        _entityResolver
            .ResolveEntityAsync(Arg.Any<ExtractedEntity>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeResolvedEntity("e-bob", "Bob")));
        _entityRepo
            .CreateExtractedFromRelationshipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("DB connection failed")));

        var sut = CreateSut();
        var result = await sut.ExtractAsync(MakeRequest(ExtractionTypes.Entities));

        result.Should().NotBeNull();
        result.Entities.Should().ContainSingle();
    }
}
