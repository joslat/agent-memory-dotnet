using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Unit tests for <see cref="PersistenceStage"/>:
/// embedding generation, repository upserts, EXTRACTED_FROM provenance wiring,
/// and fault-tolerance during persistence.
/// </summary>
public sealed class PersistenceStageTests
{
    private readonly IEmbeddingOrchestrator _orchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IEntityRepository _entityRepo = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _factRepo = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _prefRepo = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private static readonly IReadOnlyList<string> TwoMessageIds = new[] { "msg-1", "msg-2" };

    public PersistenceStageTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        _entityRepo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        _factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _prefRepo.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Preference>()));
        _relRepo.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Relationship>()));
    }

    private PersistenceStage CreateSut(ExtractionOptions? options = null) =>
        new(_orchestrator, _entityRepo, _factRepo, _prefRepo, _relRepo, _clock, _idGen,
            NullLogger<PersistenceStage>.Instance, Options.Create(options ?? new ExtractionOptions()));

    private static ExtractionStageResult EmptyResult(IReadOnlyList<string>? sourceIds = null) =>
        new()
        {
            SourceMessageIds = sourceIds ?? TwoMessageIds
        };

    // ── Entity persistence ──

    [Fact]
    public async Task PersistAsync_Entity_EmbedsAndUpserts()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = entity
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        await _orchestrator.Received(1).EmbedAsync("Alice", Arg.Any<CancellationToken>());
        await _entityRepo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        result.EntityCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_Entity_SkipsEmbeddingWhenAlreadyPresent()
    {
        var entityWithEmbedding = new Entity
        {
            EntityId = "e-1",
            Name = "Alice",
            Type = "Person",
            Confidence = 0.9,
            Embedding = new float[384], // already embedded
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = entityWithEmbedding
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _orchestrator.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Entity_CreatesExtractedFromForEachSourceMessage()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = entity
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        // 2 source messages → 2 EXTRACTED_FROM calls
        await _entityRepo.Received(TwoMessageIds.Count).CreateExtractedFromRelationshipAsync(
            "e-1", Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Fact persistence ──

    [Fact]
    public async Task PersistAsync_Fact_EmbedsAndUpserts()
    {
        _idGen.GenerateId().Returns("fact-1");

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[]
            {
                new ExtractedFact { Subject = "Alice", Predicate = "works_at", Object = "Acme", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        await _orchestrator.Received(1).EmbedAsync("Alice works_at Acme", Arg.Any<CancellationToken>());
        await _factRepo.Received(1).UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        result.FactCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_Fact_CreatesExtractedFromForEachSourceMessage()
    {
        _idGen.GenerateId().Returns("fact-1");

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[]
            {
                new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "coffee", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _factRepo.Received(TwoMessageIds.Count).CreateExtractedFromRelationshipAsync(
            "fact-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Preference persistence ──

    [Fact]
    public async Task PersistAsync_Preference_EmbedsAndUpserts()
    {
        _idGen.GenerateId().Returns("pref-1");

        var extraction = EmptyResult() with
        {
            FilteredPreferences = new[]
            {
                new ExtractedPreference { Category = "style", PreferenceText = "dark mode", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        await _orchestrator.Received(1).EmbedAsync("dark mode", Arg.Any<CancellationToken>());
        await _prefRepo.Received(1).UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        result.PreferenceCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_Preference_CreatesExtractedFromForEachSourceMessage()
    {
        _idGen.GenerateId().Returns("pref-1");

        var extraction = EmptyResult() with
        {
            FilteredPreferences = new[]
            {
                new ExtractedPreference { Category = "style", PreferenceText = "dark mode", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _prefRepo.Received(TwoMessageIds.Count).CreateExtractedFromRelationshipAsync(
            "pref-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Relationship persistence ──

    [Fact]
    public async Task PersistAsync_Relationship_ResolvesEntityIdsFromPersistedMap()
    {
        _idGen.GenerateId().Returns("rel-1");

        var alice = new Entity { EntityId = "e-alice", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var acme = new Entity { EntityId = "e-acme", Name = "Acme", Type = "Organization", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = alice,
                ["Acme"] = acme
            },
            FilteredRelationships = new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", TargetEntity = "Acme", RelationshipType = "WORKS_FOR", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _relRepo.Received(1).UpsertAsync(
            Arg.Is<Relationship>(r =>
                r.SourceEntityId == "e-alice" &&
                r.TargetEntityId == "e-acme" &&
                r.RelationshipType == "WORKS_FOR"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Relationship_SkippedWhenEntityNotInPersistedMap()
    {
        // No entities in the resolved map, so relationship can't be wired
        var extraction = EmptyResult() with
        {
            FilteredRelationships = new[]
            {
                new ExtractedRelationship { SourceEntity = "Ghost", TargetEntity = "Nobody", RelationshipType = "KNOWS", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction);

        await _relRepo.DidNotReceive().UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
    }

    // ── Fault tolerance ──

    [Fact]
    public async Task PersistAsync_EntityUpsertFails_ContinuesToNextEntity()
    {
        var alice = new Entity { EntityId = "e-alice", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var bob = new Entity { EntityId = "e-bob", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var callCount = 0;
        _entityRepo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++callCount == 1) throw new InvalidOperationException("DB error");
                return Task.FromResult(_.Arg<Entity>());
            });

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = alice,
                ["Bob"] = bob
            }
        };

        var sut = CreateSut();
        // Should not throw
        await sut.PersistAsync(extraction);

        await _entityRepo.Received(2).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_ExtractedFromFails_DoesNotFailPersistence()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        _entityRepo.CreateExtractedFromRelationshipAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("DB connection failed")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bob"] = entity
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        // Entity still counted as persisted even though provenance failed
        result.EntityCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_EmptyExtraction_ReturnsZeroCounts()
    {
        var sut = CreateSut();
        var result = await sut.PersistAsync(EmptyResult());

        result.EntityCount.Should().Be(0);
        result.FactCount.Should().Be(0);
        result.PreferenceCount.Should().Be(0);
        result.RelationshipCount.Should().Be(0);
    }

    // ── Zero items to persist ──

    [Fact]
    public async Task PersistAsync_EmptyExtraction_NoRepositoryCallsMade()
    {
        var sut = CreateSut();
        await sut.PersistAsync(EmptyResult());

        await _entityRepo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _factRepo.DidNotReceive().UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        await _prefRepo.DidNotReceive().UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        await _relRepo.DidNotReceive().UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_EmptyExtraction_NoEmbeddingCallsMade()
    {
        var sut = CreateSut();
        await sut.PersistAsync(EmptyResult());

        await _orchestrator.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _orchestrator.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _orchestrator.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Fact persistence fault tolerance ──

    [Fact]
    public async Task PersistAsync_FactUpsertFails_ContinuesToNextFact()
    {
        _idGen.GenerateId().Returns("fact-1", "fact-2");

        var factCallCount = 0;
        _factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++factCallCount == 1) throw new InvalidOperationException("DB error");
                return Task.FromResult(_.Arg<Fact>());
            });

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[]
            {
                new ExtractedFact { Subject = "A", Predicate = "b", Object = "c", Confidence = 0.9 },
                new ExtractedFact { Subject = "X", Predicate = "y", Object = "z", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        // Should not throw
        var result = await sut.PersistAsync(extraction);

        await _factRepo.Received(2).UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
        result.FactCount.Should().Be(1); // only the second fact succeeded
    }

    // ── Preference persistence fault tolerance ──

    [Fact]
    public async Task PersistAsync_PreferenceUpsertFails_ContinuesToNext()
    {
        _idGen.GenerateId().Returns("pref-1", "pref-2");

        var prefCallCount = 0;
        _prefRepo.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++prefCallCount == 1) throw new InvalidOperationException("DB error");
                return Task.FromResult(_.Arg<Preference>());
            });

        var extraction = EmptyResult() with
        {
            FilteredPreferences = new[]
            {
                new ExtractedPreference { Category = "a", PreferenceText = "text1", Confidence = 0.9 },
                new ExtractedPreference { Category = "b", PreferenceText = "text2", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        await _prefRepo.Received(2).UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>());
        result.PreferenceCount.Should().Be(1);
    }

    // ── Relationship persistence fault tolerance ──

    [Fact]
    public async Task PersistAsync_RelationshipUpsertFails_ContinuesToNext()
    {
        _idGen.GenerateId().Returns("rel-1", "rel-2");

        var alice = new Entity { EntityId = "e-alice", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var bob = new Entity { EntityId = "e-bob", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var charlie = new Entity { EntityId = "e-charlie", Name = "Charlie", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var relCallCount = 0;
        _relRepo.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++relCallCount == 1) throw new InvalidOperationException("DB error");
                return Task.FromResult(_.Arg<Relationship>());
            });

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = alice,
                ["Bob"] = bob,
                ["Charlie"] = charlie
            },
            FilteredRelationships = new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", TargetEntity = "Bob", RelationshipType = "KNOWS", Confidence = 0.9 },
                new ExtractedRelationship { SourceEntity = "Alice", TargetEntity = "Charlie", RelationshipType = "WORKS_WITH", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        await _relRepo.Received(2).UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>());
        result.RelationshipCount.Should().Be(1);
    }

    // ── Multiple entities, facts, prefs, relationships in one extraction ──

    [Fact]
    public async Task PersistAsync_FullExtraction_AllCountsCorrect()
    {
        _idGen.GenerateId().Returns("fact-1", "pref-1", "rel-1");

        var alice = new Entity { EntityId = "e-alice", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var bob = new Entity { EntityId = "e-bob", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = alice,
                ["Bob"] = bob
            },
            FilteredFacts = new[]
            {
                new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "coffee", Confidence = 0.9 }
            },
            FilteredPreferences = new[]
            {
                new ExtractedPreference { Category = "style", PreferenceText = "dark mode", Confidence = 0.9 }
            },
            FilteredRelationships = new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", TargetEntity = "Bob", RelationshipType = "KNOWS", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.EntityCount.Should().Be(2);
        result.FactCount.Should().Be(1);
        result.PreferenceCount.Should().Be(1);
        result.RelationshipCount.Should().Be(1);
    }

    // ── #92 Phase 3: trust-level stamping ────────────────────────────────────

    [Fact]
    public async Task PersistAsync_Entity_StampsProvidedTrustLevel()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, trustLevel: MemoryTrustLevel.ApplicationTrusted);

        await _entityRepo.Received(1).UpsertAsync(
            Arg.Is<Entity>(e => e.Metadata.GetTrustLevel() == MemoryTrustLevel.ApplicationTrusted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Fact_StampsProvidedTrustLevel()
    {
        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, trustLevel: MemoryTrustLevel.VerifiedExternal);

        await _factRepo.Received(1).UpsertAsync(
            Arg.Is<Fact>(f => f.Metadata.GetTrustLevel() == MemoryTrustLevel.VerifiedExternal),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Preference_StampsProvidedTrustLevel()
    {
        var extraction = EmptyResult() with
        {
            FilteredPreferences = new[] { new ExtractedPreference { Category = "style", PreferenceText = "dark mode", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, trustLevel: MemoryTrustLevel.ModelGenerated);

        await _prefRepo.Received(1).UpsertAsync(
            Arg.Is<Preference>(p => p.Metadata.GetTrustLevel() == MemoryTrustLevel.ModelGenerated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_NoTrustLevelArgument_DefaultsToUntrusted()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction); // trustLevel omitted

        await _entityRepo.Received(1).UpsertAsync(
            Arg.Is<Entity>(e => e.Metadata.GetTrustLevel() == MemoryTrustLevel.Untrusted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Entity_StampingTrustLevel_PreservesExistingMetadata()
    {
        var entity = new Entity
        {
            EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object> { ["custom_key"] = "custom_value" }
        };
        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, trustLevel: MemoryTrustLevel.UserProvided);

        await _entityRepo.Received(1).UpsertAsync(
            Arg.Is<Entity>(e =>
                e.Metadata.GetTrustLevel() == MemoryTrustLevel.UserProvided &&
                e.Metadata.ContainsKey("custom_key") && (string)e.Metadata["custom_key"] == "custom_value"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Entity_ResolvedOntoAlreadyTrustedExistingEntity_DoesNotDowngradeTrust()
    {
        // Trust is monotonic (#92 Phase 3 security fix): entity resolution (auto-merge/SAME_AS) can hand
        // PersistenceStage an EXISTING, previously-persisted entity (already stamped ApplicationTrusted by
        // an earlier curated import) as the resolved match for a brand-new, unrelated, lower-trust mention.
        // That later mention must never silently erase the earlier, deliberate trust elevation.
        var alreadyTrustedEntity = new Entity
        {
            EntityId = "e-1", Name = "Acme Corp", Type = "Organization", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>().WithTrustLevel(MemoryTrustLevel.ApplicationTrusted)
        };
        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Acme Corp"] = alreadyTrustedEntity }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, trustLevel: MemoryTrustLevel.UserProvided); // an ordinary later mention

        await _entityRepo.Received(1).UpsertAsync(
            Arg.Is<Entity>(e => e.Metadata.GetTrustLevel() == MemoryTrustLevel.ApplicationTrusted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Entity_ResolvedOntoLowerTrustExistingEntity_UpgradesToTheNewHigherTrust()
    {
        // The symmetric case: a curated, higher-trust import touching an entity that previously only
        // existed at a lower trust level correctly raises it -- monotonic means "never decreases",
        // not "first write wins".
        var previouslyUntrustedEntity = new Entity
        {
            EntityId = "e-1", Name = "Acme Corp", Type = "Organization", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>().WithTrustLevel(MemoryTrustLevel.UserProvided)
        };
        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Acme Corp"] = previouslyUntrustedEntity }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, trustLevel: MemoryTrustLevel.ApplicationTrusted);

        await _entityRepo.Received(1).UpsertAsync(
            Arg.Is<Entity>(e => e.Metadata.GetTrustLevel() == MemoryTrustLevel.ApplicationTrusted),
            Arg.Any<CancellationToken>());
    }

    // ── #92 Phase 5: monotonic trust for facts on exact-triple re-extraction ─

    [Fact]
    public async Task PersistAsync_Fact_ExistingSameTripleAlreadyTrusted_DoesNotDowngradeTrust()
    {
        // Trust is monotonic for facts too (#92 Phase 5), mirroring entities (Phase 3): the repository's
        // Upsert MERGEs on the exact {subject,predicate,object,owner} triple and ON MATCH unconditionally
        // overwrites metadata, so PersistenceStage now pre-fetches any existing fact with the same triple
        // and never lets a later, ordinary, lower-trust re-extraction erase an earlier deliberate elevation.
        var alreadyTrustedFact = new Fact
        {
            FactId = "f-existing", Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>().WithTrustLevel(MemoryTrustLevel.ApplicationTrusted)
        };
        _factRepo.FindByTripleAsync("user", "lives in", "Zurich", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(alreadyTrustedFact);

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        // ownerId set: the pre-fetch only runs for owner-scoped facts (see the disclosed limitation in
        // PersistenceStage.cs -- an owner-less lookup would risk a cross-tenant read).
        await sut.PersistAsync(extraction, ownerId: "alice", trustLevel: MemoryTrustLevel.UserProvided); // an ordinary later mention

        await _factRepo.Received(1).UpsertAsync(
            Arg.Is<Fact>(f => f.Metadata.GetTrustLevel() == MemoryTrustLevel.ApplicationTrusted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Fact_ExistingSameTripleLowerTrust_UpgradesToTheNewHigherTrust()
    {
        var previouslyUntrustedFact = new Fact
        {
            FactId = "f-existing", Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>().WithTrustLevel(MemoryTrustLevel.UserProvided)
        };
        _factRepo.FindByTripleAsync("user", "lives in", "Zurich", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(previouslyUntrustedFact);

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, ownerId: "alice", trustLevel: MemoryTrustLevel.ApplicationTrusted);

        await _factRepo.Received(1).UpsertAsync(
            Arg.Is<Fact>(f => f.Metadata.GetTrustLevel() == MemoryTrustLevel.ApplicationTrusted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Fact_ExistingSameTriple_OtherMetadataKeysArePreserved()
    {
        var existingFact = new Fact
        {
            FactId = "f-existing", Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object> { ["custom_key"] = "custom_value" }
        };
        _factRepo.FindByTripleAsync("user", "lives in", "Zurich", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(existingFact);

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, ownerId: "alice", trustLevel: MemoryTrustLevel.VerifiedExternal);

        await _factRepo.Received(1).UpsertAsync(
            Arg.Is<Fact>(f =>
                f.Metadata.GetTrustLevel() == MemoryTrustLevel.VerifiedExternal &&
                f.Metadata.ContainsKey("custom_key") && (string)f.Metadata["custom_key"] == "custom_value"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Fact_NoExistingTripleFound_LooksUpByTheExtractedTripleAndOwnerScope()
    {
        // Wiring check: the pre-fetch must query by the extracted triple, scoped to the same owner the
        // fact is about to be persisted under -- otherwise it could leak another owner's trust level or
        // never find same-owner history at all.
        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "Ada", Predicate = "works_at", Object = "Acme", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, ownerId: "alice", trustLevel: MemoryTrustLevel.UserProvided);

        await _factRepo.Received(1).FindByTripleAsync(
            "Ada", "works_at", "Acme",
            Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_Fact_SharedGlobalFact_SkipsThePreFetch_DisclosedLimitation()
    {
        // Disclosed limitation: FindByTripleAsync's MemoryScope? parameter treats an owner-less lookup as
        // "search across every owner" (the read/recall convention), not "shared bucket only" (the write
        // convention for a null ownerId) -- so pre-fetching for a shared/global fact would risk adopting
        // another owner's trust level. Shared/global facts therefore don't get monotonic-trust protection
        // yet; they keep today's fresh-stamp behavior, and the lookup must not be attempted at all.
        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "user", Predicate = "lives in", Object = "Zurich", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        await sut.PersistAsync(extraction, ownerId: null, trustLevel: MemoryTrustLevel.UserProvided);

        await _factRepo.DidNotReceive().FindByTripleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _factRepo.Received(1).UpsertAsync(
            Arg.Is<Fact>(f => f.Metadata.GetTrustLevel() == MemoryTrustLevel.UserProvided),
            Arg.Any<CancellationToken>());
    }

    // ── #101: structured ingestion outcomes ─────────────────────────────────

    [Fact]
    public async Task PersistAsync_EntitySucceeds_RecordsSucceededOutcome()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Entity &&
            o.Stage == IngestionStage.Persistence &&
            o.Status == IngestionItemStatus.Succeeded &&
            o.SourceKey == "Alice" &&
            o.PersistedId == "e-1");
    }

    [Fact]
    public async Task PersistAsync_EntityEmbeddingFails_RecordsFailedOutcomeAtEmbeddingStage_NotPersistence()
    {
        // #101 review: embedding failures must be distinguishable from graph-write failures — a caller
        // building retry/alerting logic on Stage/ErrorCode needs to tell "the embedding provider is down"
        // apart from "the Neo4j write failed".
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<float[]>(new InvalidOperationException("embedding provider down")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Entity &&
            o.Stage == IngestionStage.Embedding &&
            o.Status == IngestionItemStatus.Failed &&
            o.ErrorCode == MemoryErrorCodes.EmbeddingGenerationFailed);
        await _entityRepo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_FactEmbeddingFails_RecordsFailedOutcomeAtEmbeddingStage_NotPersistence()
    {
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<float[]>(new InvalidOperationException("embedding provider down")));

        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "A", Predicate = "b", Object = "c", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Fact &&
            o.Stage == IngestionStage.Embedding &&
            o.Status == IngestionItemStatus.Failed &&
            o.ErrorCode == MemoryErrorCodes.EmbeddingGenerationFailed);
        await _factRepo.DidNotReceive().UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_FailFast_EmbeddingFails_ThrowsMemoryIngestionException()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<float[]>(new InvalidOperationException("embedding provider down")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut(new ExtractionOptions { FailureMode = IngestionFailureMode.FailFast });
        var act = () => sut.PersistAsync(extraction);

        var ex = await act.Should().ThrowAsync<MemoryIngestionException>();
        ex.Which.CompletedOutcomes.Should().ContainSingle(o => o.Stage == IngestionStage.Embedding);
    }

    [Fact]
    public async Task PersistAsync_EntityUpsertFails_RecordsFailedOutcome()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        _entityRepo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Entity>(new InvalidOperationException("DB error")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Entity &&
            o.Stage == IngestionStage.Persistence &&
            o.Status == IngestionItemStatus.Failed &&
            o.ErrorCode == MemoryErrorCodes.EntityPersistenceFailed &&
            o.Retryable);
    }

    [Fact]
    public async Task PersistAsync_ExtractedFromFails_RecordsProvenanceFailure_SeparateFromPersistenceSuccess()
    {
        // Required behavior from #101: a fact/entity node can persist successfully while its
        // provenance edge fails — both outcomes must be visible, distinctly staged.
        var entity = new Entity { EntityId = "e-1", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        _entityRepo.CreateExtractedFromRelationshipAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("DB connection failed")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Bob"] = entity }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().Contain(o =>
            o.Kind == MemoryItemKind.Entity && o.Stage == IngestionStage.Persistence && o.Status == IngestionItemStatus.Succeeded);
        result.Outcomes.Should().Contain(o =>
            o.Kind == MemoryItemKind.Entity && o.Stage == IngestionStage.Provenance && o.Status == IngestionItemStatus.Failed &&
            o.ErrorCode == MemoryErrorCodes.ProvenancePersistenceFailed);
    }

    [Fact]
    public async Task PersistAsync_FactSucceeds_RecordsSucceededOutcome()
    {
        _idGen.GenerateId().Returns("fact-1");
        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "coffee", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Fact &&
            o.Stage == IngestionStage.Persistence &&
            o.Status == IngestionItemStatus.Succeeded &&
            o.PersistedId == "fact-1");
    }

    [Fact]
    public async Task PersistAsync_FactUpsertFails_RecordsFailedOutcome()
    {
        _factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Fact>(new InvalidOperationException("DB error")));
        var extraction = EmptyResult() with
        {
            FilteredFacts = new[] { new ExtractedFact { Subject = "A", Predicate = "b", Object = "c", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Fact &&
            o.Stage == IngestionStage.Persistence &&
            o.Status == IngestionItemStatus.Failed &&
            o.ErrorCode == MemoryErrorCodes.FactPersistenceFailed);
    }

    [Fact]
    public async Task PersistAsync_PreferenceUpsertFails_RecordsFailedOutcome()
    {
        _prefRepo.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Preference>(new InvalidOperationException("DB error")));
        var extraction = EmptyResult() with
        {
            FilteredPreferences = new[] { new ExtractedPreference { Category = "a", PreferenceText = "text1", Confidence = 0.9 } }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Preference &&
            o.Stage == IngestionStage.Persistence &&
            o.Status == IngestionItemStatus.Failed &&
            o.ErrorCode == MemoryErrorCodes.PreferencePersistenceFailed);
    }

    [Fact]
    public async Task PersistAsync_RelationshipEndpointNotPersisted_RecordsSkippedOutcome()
    {
        var extraction = EmptyResult() with
        {
            FilteredRelationships = new[]
            {
                new ExtractedRelationship { SourceEntity = "Ghost", TargetEntity = "Nobody", RelationshipType = "KNOWS", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Relationship &&
            o.Stage == IngestionStage.RelationshipPersistence &&
            o.Status == IngestionItemStatus.Skipped &&
            o.ErrorCode == MemoryErrorCodes.RelationshipEndpointNotPersisted);
    }

    [Fact]
    public async Task PersistAsync_RelationshipUpsertFails_RecordsFailedOutcome()
    {
        var alice = new Entity { EntityId = "e-alice", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        var bob = new Entity { EntityId = "e-bob", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        _relRepo.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Relationship>(new InvalidOperationException("DB error")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = alice, ["Bob"] = bob },
            FilteredRelationships = new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", TargetEntity = "Bob", RelationshipType = "KNOWS", Confidence = 0.9 }
            }
        };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().ContainSingle(o =>
            o.Kind == MemoryItemKind.Relationship &&
            o.Stage == IngestionStage.Persistence &&
            o.Status == IngestionItemStatus.Failed &&
            o.ErrorCode == MemoryErrorCodes.RelationshipPersistenceFailed);
    }

    [Fact]
    public async Task PersistAsync_PriorOutcomesFromExtractionStage_ArePreservedInResult()
    {
        var priorOutcome = new IngestionItemOutcome
        {
            Kind = MemoryItemKind.Entity,
            Stage = IngestionStage.Extraction,
            Status = IngestionItemStatus.Failed,
            SourceKey = "some-extractor",
            ErrorCode = MemoryErrorCodes.EntityExtractionFailed,
        };
        var extraction = EmptyResult() with { Outcomes = new[] { priorOutcome } };

        var sut = CreateSut();
        var result = await sut.PersistAsync(extraction);

        result.Outcomes.Should().Contain(priorOutcome);
    }

    [Fact]
    public async Task PersistAsync_FailFast_EntityPersistenceFails_ThrowsMemoryIngestionException()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        _entityRepo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Entity>(new InvalidOperationException("DB error")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut(new ExtractionOptions { FailureMode = IngestionFailureMode.FailFast });
        var act = () => sut.PersistAsync(extraction);

        var ex = await act.Should().ThrowAsync<MemoryIngestionException>();
        ex.Which.CompletedOutcomes.Should().ContainSingle(o =>
            o.Status == IngestionItemStatus.Failed && o.ErrorCode == MemoryErrorCodes.EntityPersistenceFailed);
    }

    [Fact]
    public async Task PersistAsync_FailFast_ProvenanceFails_ThrowsMemoryIngestionException()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Bob", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        _entityRepo.CreateExtractedFromRelationshipAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("DB connection failed")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Bob"] = entity }
        };

        var sut = CreateSut(new ExtractionOptions { FailureMode = IngestionFailureMode.FailFast });
        var act = () => sut.PersistAsync(extraction);

        var ex = await act.Should().ThrowAsync<MemoryIngestionException>();
        ex.Which.CompletedOutcomes.Should().Contain(o =>
            o.Stage == IngestionStage.Provenance && o.Status == IngestionItemStatus.Failed);
        // The entity's own persistence success must still be visible even though provenance failed fast.
        ex.Which.CompletedOutcomes.Should().Contain(o =>
            o.Stage == IngestionStage.Persistence && o.Status == IngestionItemStatus.Succeeded);
    }

    [Fact]
    public async Task PersistAsync_BestEffortIsDefault_DoesNotThrowOnPersistenceFailure()
    {
        var entity = new Entity { EntityId = "e-1", Name = "Alice", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow };
        _entityRepo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Entity>(new InvalidOperationException("DB error")));

        var extraction = EmptyResult() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = entity }
        };

        var sut = CreateSut(); // default options — BestEffort
        var act = () => sut.PersistAsync(extraction);

        await act.Should().NotThrowAsync();
    }
}
