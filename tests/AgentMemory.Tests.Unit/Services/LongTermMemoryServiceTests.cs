using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class LongTermMemoryServiceTests
{
    private readonly IEntityRepository _entityRepo;
    private readonly IFactRepository _factRepo;
    private readonly IPreferenceRepository _prefRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;

    public LongTermMemoryServiceTests()
    {
        _entityRepo = Substitute.For<IEntityRepository>();
        _factRepo = Substitute.For<IFactRepository>();
        _prefRepo = Substitute.For<IPreferenceRepository>();
        _relRepo = Substitute.For<IRelationshipRepository>();
        _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();

        _embeddingOrchestrator
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[1536]));
        _embeddingOrchestrator
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[1536]));
        _embeddingOrchestrator
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[1536]));

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
        new(_entityRepo, _factRepo, _prefRepo, _relRepo, _embeddingOrchestrator,
            options ?? Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance));

    private LongTermMemoryService CreateSutWithIsolationMode(MemoryIsolationMode mode) =>
        new(_entityRepo, _factRepo, _prefRepo, _relRepo, _embeddingOrchestrator,
            Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions { Mode = mode }), NullLogger<DefaultMemoryIsolationPolicy>.Instance));

    // ---- Entity tests ----

    [Fact]
    public async Task AddEntityAsync_GeneratesEmbeddingWhenEnabled()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { GenerateEntityEmbeddings = true }));
        var entity = CreateEntity("e-1", withEmbedding: false);

        await sut.AddEntityAsync(entity);

        await _embeddingOrchestrator
            .Received(1)
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddEntityAsync_SkipsEmbeddingWhenAlreadyProvided()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { GenerateEntityEmbeddings = true }));
        var entity = CreateEntity("e-1", withEmbedding: true);

        await sut.AddEntityAsync(entity);

        await _embeddingOrchestrator
            .DidNotReceive()
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public async Task RecordEntityFeedbackAsync_Positive_AppliesConfiguredDelta()
    {
        _entityRepo.ApplyConfidenceDeltaAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(CreateEntity("e-1")));
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { FeedbackConfidenceDelta = 0.1 }));

        await sut.RecordEntityFeedbackAsync("e-1", positive: true);

        await _entityRepo.Received(1).ApplyConfidenceDeltaAsync("e-1", 0.1, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordEntityFeedbackAsync_Negative_AppliesNegativeDelta()
    {
        _entityRepo.ApplyConfidenceDeltaAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(CreateEntity("e-1")));
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { FeedbackConfidenceDelta = 0.2 }));

        await sut.RecordEntityFeedbackAsync("e-1", positive: false);

        await _entityRepo.Received(1).ApplyConfidenceDeltaAsync("e-1", -0.2, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordEntityFeedbackAsync_ExplicitDelta_IsSignedByDirection()
    {
        _entityRepo.ApplyConfidenceDeltaAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(CreateEntity("e-1")));
        var sut = CreateSut();

        await sut.RecordEntityFeedbackAsync("e-1", positive: false, delta: 0.3);

        await _entityRepo.Received(1).ApplyConfidenceDeltaAsync("e-1", -0.3, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecordEntityFeedbackAsync_BlankId_Throws(string id)
    {
        var sut = CreateSut();
        await sut.Invoking(s => s.RecordEntityFeedbackAsync(id, true)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetEntitiesByNameAsync_DelegatesToRepository()
    {
        _entityRepo
            .GetByNameAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        var sut = CreateSut();

        await sut.GetEntitiesByNameAsync("Alice");

        await _entityRepo
            .Received(1)
            .GetByNameAsync("Alice", Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchEntitiesAsync_DelegatesToRepositoryAndStripsScores()
    {
        var entity = CreateEntity("e-1");
        _entityRepo
            .SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
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

        await _embeddingOrchestrator
            .Received(1)
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            .GetByCategoryAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        var sut = CreateSut();

        await sut.GetPreferencesByCategoryAsync("style");

        await _prefRepo
            .Received(1)
            .GetByCategoryAsync("style", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchPreferencesAsync_StripsScores()
    {
        var pref = CreatePreference("p-1");
        _prefRepo
            .SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
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

        await _embeddingOrchestrator
            .Received(1)
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFactsBySubjectAsync_DelegatesToRepository()
    {
        _factRepo
            .GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        var sut = CreateSut();

        await sut.GetFactsBySubjectAsync("Alice");

        await _factRepo
            .Received(1)
            .GetBySubjectAsync("Alice", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchFactsAsync_StripsScores()
    {
        var fact = CreateFact("f-1");
        _factRepo
            .SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
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
            .GetByEntityAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(Array.Empty<Relationship>()));
        var sut = CreateSut();

        await sut.GetEntityRelationshipsAsync("e-1");

        await _relRepo
            .Received(1)
            .GetByEntityAsync("e-1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ---- Dedup-on-create (PR#97) ----

    [Fact]
    public async Task AddFactAsync_WhenDuplicateFound_ReinforcesInsteadOfCreating()
    {
        var existing = CreateFact("f-existing"); // confidence 0.9
        _factRepo.FindDuplicateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Fact?>(existing));
        _factRepo.MarkDeduplicatedAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<Fact?>(existing with { Confidence = ci.ArgAt<double>(1) }));

        var sut = CreateSut();
        var result = await sut.AddFactAsync(CreateFact("f-new"));

        await _factRepo.Received(1).MarkDeduplicatedAsync("f-existing", Arg.Is<double>(c => c > 0.9), Arg.Any<CancellationToken>());
        await _factRepo.DidNotReceive().UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        result.FactId.Should().Be("f-existing");
    }

    [Fact]
    public async Task AddFactAsync_WhenNoDuplicate_Upserts()
    {
        _factRepo.FindDuplicateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Fact?>(null));

        await CreateSut().AddFactAsync(CreateFact("f-new"));

        await _factRepo.Received(1).UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        await _factRepo.DidNotReceive().MarkDeduplicatedAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddFactAsync_DeduplicateDisabled_SkipsDuplicateLookup()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { DeduplicateOnCreate = false }));

        await sut.AddFactAsync(CreateFact("f-new"));

        await _factRepo.DidNotReceive().FindDuplicateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _factRepo.Received(1).UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddFactAsync_ConcurrentSameKeyAcrossServiceInstances_SerializesDedupDecision()
    {
        var sync = new object();
        Fact? persisted = null;
        var activeLookups = 0;
        var maxActiveLookups = 0;
        var upserts = 0;
        var reinforcements = 0;

        _factRepo
            .FindDuplicateAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(),
                Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Fact? observed;
                lock (sync)
                {
                    activeLookups++;
                    maxActiveLookups = Math.Max(maxActiveLookups, activeLookups);
                    observed = persisted;
                }

                await Task.Delay(50);
                lock (sync) activeLookups--;
                return observed;
            });
        _factRepo
            .UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var fact = call.Arg<Fact>();
                lock (sync)
                {
                    upserts++;
                    persisted = fact;
                }
                return Task.FromResult(fact);
            });
        _factRepo
            .MarkDeduplicatedAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (sync)
                {
                    reinforcements++;
                    return Task.FromResult<Fact?>(persisted! with { Confidence = call.ArgAt<double>(1) });
                }
            });

        var first = CreateSut().AddFactAsync(CreateFact("f-race-1") with { OwnerId = "race-owner" });
        var second = CreateSut().AddFactAsync(CreateFact("f-race-2") with { OwnerId = "race-owner" });
        await Task.WhenAll(first, second);

        maxActiveLookups.Should().Be(1, "same-key dedup must be serialized across service scopes");
        upserts.Should().Be(1);
        reinforcements.Should().Be(1);
    }

    [Fact]
    public async Task AddPreferenceAsync_WhenDuplicateFound_ReinforcesInsteadOfCreating()
    {
        var existing = CreatePreference("p-existing");
        _prefRepo.FindDuplicateAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Preference?>(existing));
        _prefRepo.MarkDeduplicatedAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<Preference?>(existing with { Confidence = ci.ArgAt<double>(1) }));

        var result = await CreateSut().AddPreferenceAsync(CreatePreference("p-new"));

        await _prefRepo.Received(1).MarkDeduplicatedAsync("p-existing", Arg.Is<double>(c => c > 0.9), Arg.Any<CancellationToken>());
        await _prefRepo.DidNotReceive().UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        result.PreferenceId.Should().Be("p-existing");
    }

    [Fact]
    public async Task AddFactAsync_WhenDuplicateVanishesBeforeReinforce_FallsThroughToCreate()
    {
        // FindDuplicate returns a dup, but the node is concurrently hard-deleted before reinforce, so
        // MarkDeduplicatedAsync returns null. The add must NOT throw — it falls through to create the node.
        var existing = CreateFact("f-existing");
        _factRepo.FindDuplicateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Fact?>(existing));
        _factRepo.MarkDeduplicatedAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Fact?>(null)); // vanished

        var result = await CreateSut().AddFactAsync(CreateFact("f-new"));

        await _factRepo.Received(1).UpsertAsync(Arg.Is<Fact>(f => f.FactId == "f-new"), Arg.Any<CancellationToken>());
        result.FactId.Should().Be("f-new");
    }

    [Fact]
    public async Task AddPreferenceAsync_WhenDuplicateVanishesBeforeReinforce_FallsThroughToCreate()
    {
        var existing = CreatePreference("p-existing");
        _prefRepo.FindDuplicateAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Preference?>(existing));
        _prefRepo.MarkDeduplicatedAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Preference?>(null)); // vanished

        var result = await CreateSut().AddPreferenceAsync(CreatePreference("p-new"));

        await _prefRepo.Received(1).UpsertAsync(Arg.Is<Preference>(p => p.PreferenceId == "p-new"), Arg.Any<CancellationToken>());
        result.PreferenceId.Should().Be("p-new");
    }

    // ---- degraded (empty) embedding skips dedup vector lookup ----

    [Fact]
    public async Task AddFactAsync_DegradedEmptyEmbedding_SkipsDuplicateLookup_AndStillCreates()
    {
        // A transient embed failure degrades to an empty vector; it must NOT be handed to the dedup vector
        // index (which would throw a dimension mismatch and abort the add). Skip dedup, still create.
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<float>()));
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { GenerateFactEmbeddings = true, DeduplicateOnCreate = true }));

        var result = await sut.AddFactAsync(CreateFact("f-new", withEmbedding: false));

        await _factRepo.DidNotReceive().FindDuplicateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _factRepo.Received(1).UpsertAsync(Arg.Is<Fact>(f => f.FactId == "f-new"), Arg.Any<CancellationToken>());
        result.FactId.Should().Be("f-new");
    }

    [Fact]
    public async Task AddPreferenceAsync_DegradedEmptyEmbedding_SkipsDuplicateLookup_AndStillCreates()
    {
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<float>()));
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { GeneratePreferenceEmbeddings = true, DeduplicateOnCreate = true }));

        var result = await sut.AddPreferenceAsync(CreatePreference("p-new", withEmbedding: false));

        await _prefRepo.DidNotReceive().FindDuplicateAsync(
            Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _prefRepo.Received(1).UpsertAsync(Arg.Is<Preference>(p => p.PreferenceId == "p-new"), Arg.Any<CancellationToken>());
        result.PreferenceId.Should().Be("p-new");
    }

    // ---- MinConfidenceThreshold gating ----

    [Fact]
    public async Task AddEntityAsync_BelowMinConfidence_NotPersisted_ReturnsItem()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { MinConfidenceThreshold = 0.7 }));
        var entity = CreateEntity("e-low") with { Confidence = 0.6 };

        var result = await sut.AddEntityAsync(entity);

        await _entityRepo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _embeddingOrchestrator.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Not BeSameAs: #100 Stage 2's owner-resolution guard clause always produces a new `with`-cloned
        // record (even when the resolved owner id is unchanged), so identity is no longer preserved --
        // only value equality is a meaningful contract here.
        result.Should().Be(entity);
    }

    [Fact]
    public async Task AddEntityAsync_AtMinConfidence_IsPersisted()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { MinConfidenceThreshold = 0.7 }));
        var entity = CreateEntity("e-boundary") with { Confidence = 0.7 };

        await sut.AddEntityAsync(entity);

        await _entityRepo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddFactAsync_BelowMinConfidence_NotPersisted_AndSkipsDuplicateLookup()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { MinConfidenceThreshold = 0.7 }));
        var fact = CreateFact("f-low") with { Confidence = 0.6 };

        var result = await sut.AddFactAsync(fact);

        await _factRepo.DidNotReceive().UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        await _factRepo.DidNotReceive().FindDuplicateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        // Not BeSameAs: #100 Stage 2's owner-resolution guard clause always produces a new `with`-cloned
        // record (even when the resolved owner id is unchanged), so identity is no longer preserved --
        // only value equality is a meaningful contract here.
        result.Should().Be(fact);
    }

    [Fact]
    public async Task AddPreferenceAsync_BelowMinConfidence_NotPersisted_AndSkipsDuplicateLookup()
    {
        var sut = CreateSut(Options.Create(new LongTermMemoryOptions { MinConfidenceThreshold = 0.7 }));
        var pref = CreatePreference("p-low") with { Confidence = 0.6 };

        var result = await sut.AddPreferenceAsync(pref);

        await _prefRepo.DidNotReceive().UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        await _prefRepo.DidNotReceive().FindDuplicateAsync(
            Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<string?>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        // Not BeSameAs: #100 Stage 2's owner-resolution guard clause always produces a new `with`-cloned
        // record (even when the resolved owner id is unchanged), so identity is no longer preserved --
        // only value equality is a meaningful contract here.
        result.Should().Be(pref);
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
        _prefRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await sut.DeletePreferenceAsync("pref-123");

        await _prefRepo.Received(1).DeleteAsync("pref-123", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePreferenceAsync_DelegatesToRepositoryWithAnyId()
    {
        var sut = CreateSut();
        _prefRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await sut.DeletePreferenceAsync("any-id-value");

        await _prefRepo.Received(1).DeleteAsync("any-id-value", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePreferenceAsync_RepositoryIsCalled()
    {
        var sut = CreateSut();
        _prefRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await sut.DeletePreferenceAsync("pref-to-delete");

        await _prefRepo.Received(1).DeleteAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ── Invalidation & supersession (D5 / D7) delegation ─────────────────

    [Fact]
    public async Task InvalidateFactAsync_DelegatesToFactRepo_WithScope_AndReturnsResult()
    {
        var scope = MemoryScope.For("alice");
        _factRepo.InvalidateAsync("f1", scope, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var sut = CreateSut();

        (await sut.InvalidateFactAsync("f1", scope)).Should().BeTrue();
        await _factRepo.Received(1).InvalidateAsync("f1", scope, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateEntityAsync_DelegatesToEntityRepo()
    {
        _entityRepo.InvalidateAsync("e1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var sut = CreateSut();

        (await sut.InvalidateEntityAsync("e1")).Should().BeTrue();
        await _entityRepo.Received(1).InvalidateAsync("e1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidatePreferenceAsync_DelegatesToPreferenceRepo()
    {
        _prefRepo.InvalidateAsync("p1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        var sut = CreateSut();

        (await sut.InvalidatePreferenceAsync("p1")).Should().BeFalse();
        await _prefRepo.Received(1).InvalidateAsync("p1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SupersedeFactAsync_DelegatesToFactRepo_WithBothIdsAndScope()
    {
        var scope = MemoryScope.For("alice");
        _factRepo.SupersedeAsync("loser", "winner", scope, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var sut = CreateSut();

        (await sut.SupersedeFactAsync("loser", "winner", scope)).Should().BeTrue();
        await _factRepo.Received(1).SupersedeAsync("loser", "winner", scope, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SupersedePreferenceAsync_DelegatesToPreferenceRepo()
    {
        _prefRepo.SupersedeAsync("loser", "winner", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var sut = CreateSut();

        (await sut.SupersedePreferenceAsync("loser", "winner")).Should().BeTrue();
        await _prefRepo.Received(1).SupersedeAsync("loser", "winner", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ── #100 Stage 2: every write/read now goes through IMemoryIsolationPolicy, not just
    // invalidate/supersede — StrictMultiTenant fails closed before any repository call. ──

    [Fact]
    public async Task AddEntityAsync_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);
        var entity = CreateEntity("e-strict");

        var act = () => sut.AddEntityAsync(entity);

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _entityRepo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddEntityAsync_WithOwner_StrictMode_Succeeds()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);
        var entity = CreateEntity("e-strict-owned") with { OwnerId = "alice" };

        await sut.AddEntityAsync(entity);

        await _entityRepo.Received(1).UpsertAsync(
            Arg.Is<Entity>(e => e.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddPreferenceAsync_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);
        var pref = CreatePreference("p-strict");

        var act = () => sut.AddPreferenceAsync(pref);

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _prefRepo.DidNotReceive().UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddPreferenceAsync_WithOwner_StrictMode_Succeeds()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);
        var pref = CreatePreference("p-strict-owned") with { OwnerId = "alice" };

        await sut.AddPreferenceAsync(pref);

        await _prefRepo.Received(1).UpsertAsync(
            Arg.Is<Preference>(p => p.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddFactAsync_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);
        var fact = CreateFact("f-strict");

        var act = () => sut.AddFactAsync(fact);

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _factRepo.DidNotReceive().UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddFactAsync_WithOwner_StrictMode_Succeeds()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);
        var fact = CreateFact("f-strict-owned") with { OwnerId = "alice" };

        await sut.AddFactAsync(fact);

        await _factRepo.Received(1).UpsertAsync(
            Arg.Is<Fact>(f => f.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRelationshipAsync_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);
        var relationship = CreateRelationship("r-strict");

        var act = () => sut.AddRelationshipAsync(relationship);

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _relRepo.DidNotReceive().UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRelationshipAsync_WithOwner_StrictMode_Succeeds()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);
        var relationship = CreateRelationship("r-strict-owned") with { OwnerId = "alice" };

        await sut.AddRelationshipAsync(relationship);

        await _relRepo.Received(1).UpsertAsync(
            Arg.Is<Relationship>(r => r.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordEntityFeedbackAsync_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);

        var act = () => sut.RecordEntityFeedbackAsync("e1", positive: true);

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _entityRepo.DidNotReceive().ApplyConfidenceDeltaAsync(
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // Representative sample of read-shaped methods proving the shared Resolve() wiring reaches the
    // repository call — all of them share the exact same private helper, so this is not exhaustive
    // per-method coverage (the live-Neo4j integration tests prove per-tool Alice/Bob isolation instead).

    [Fact]
    public async Task GetEntitiesByNameAsync_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);

        var act = () => sut.GetEntitiesByNameAsync("Alice");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _entityRepo.DidNotReceive().GetByNameAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchFactsAsync_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);

        var act = () => sut.SearchFactsAsync(new float[1536]);

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _factRepo.DidNotReceive().SearchByVectorAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePreferenceAsync_Unscoped_StrictMode_ThrowsBeforeRepositoryCall()
    {
        var sut = CreateSutWithIsolationMode(MemoryIsolationMode.StrictMultiTenant);

        var act = () => sut.DeletePreferenceAsync("pref-1");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _prefRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }
}
