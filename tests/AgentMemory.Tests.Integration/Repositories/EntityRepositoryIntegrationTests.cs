using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class EntityRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _repo;

    private static readonly float[] TestEmbedding = [0.4f, 0.3f, 0.2f, 0.1f];
    private static readonly float[] QueryEmbedding = [0.4f, 0.3f, 0.2f, 0.1f];

    public EntityRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jEntityRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertAsync_CreatesEntity_WithAllRequiredProperties()
    {
        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Alice Smith",
            Type = "Person",
            Confidence = 0.95,
            Description = "A software engineer",
            Aliases = ["Alice", "A. Smith"],
            CreatedAtUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await _repo.UpsertAsync(entity);

        result.EntityId.Should().Be(entity.EntityId);
        result.Name.Should().Be("Alice Smith");
        result.Type.Should().Be("Person");
        result.Confidence.Should().Be(0.95);
        result.Description.Should().Be("A software engineer");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsEntity_WhenExists()
    {
        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Bob Jones",
            Type = "Person",
            Confidence = 0.8,
            Description = "A project manager",
            Aliases = ["Bob"],
            CreatedAtUtc = new DateTimeOffset(2025, 3, 15, 12, 0, 0, TimeSpan.Zero)
        };
        await _repo.UpsertAsync(entity);

        var result = await _repo.GetByIdAsync(entity.EntityId);

        result.Should().NotBeNull();
        result!.EntityId.Should().Be(entity.EntityId);
        result.Name.Should().Be("Bob Jones");
        result.Type.Should().Be("Person");
        result.Description.Should().Be("A project manager");
        result.CreatedAtUtc.Should().BeCloseTo(entity.CreatedAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync("entity-does-not-exist");

        result.Should().BeNull();
    }

    // ── ApplyConfidenceDeltaAsync (entity feedback) + UpdatedAtUtc read-back ──

    private async Task<string> SeedEntityAsync(double confidence)
    {
        var id = $"entity-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Entity
        {
            EntityId = id, Name = "Feedback Subject", Type = "Concept",
            Confidence = confidence, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        return id;
    }

    [Fact]
    public async Task ApplyConfidenceDeltaAsync_IncreasesConfidence_AndStampsUpdatedAt()
    {
        var id = await SeedEntityAsync(0.5);

        var updated = await _repo.ApplyConfidenceDeltaAsync(id, 0.2);

        updated.Should().NotBeNull();
        updated!.Confidence.Should().BeApproximately(0.7, 1e-9);
        updated.UpdatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyConfidenceDeltaAsync_ClampsToOne()
    {
        var id = await SeedEntityAsync(0.95);

        var updated = await _repo.ApplyConfidenceDeltaAsync(id, 0.5);

        updated!.Confidence.Should().Be(1.0);
    }

    [Fact]
    public async Task ApplyConfidenceDeltaAsync_ClampsToZero()
    {
        var id = await SeedEntityAsync(0.1);

        var updated = await _repo.ApplyConfidenceDeltaAsync(id, -0.5);

        updated!.Confidence.Should().Be(0.0);
    }

    [Fact]
    public async Task ApplyConfidenceDeltaAsync_ReturnsNull_WhenEntityMissing()
    {
        (await _repo.ApplyConfidenceDeltaAsync("entity-does-not-exist", 0.1)).Should().BeNull();
    }

    [Fact]
    public async Task ApplyConfidenceDeltaAsync_ForeignOwnerScope_DoesNotMutate_OtherOwnersEntity()
    {
        var id = $"entity-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Entity
        {
            EntityId = id, Name = "Alice private", Type = "Concept",
            Confidence = 0.5, OwnerId = "alice", CreatedAtUtc = DateTimeOffset.UtcNow
        });

        // Bob's scope must NOT match Alice's private entity (R1): no row, no mutation.
        (await _repo.ApplyConfidenceDeltaAsync(id, 0.3, MemoryScope.For("bob"))).Should().BeNull();

        // Alice's own scope can.
        var aliceResult = await _repo.ApplyConfidenceDeltaAsync(id, 0.3, MemoryScope.For("alice"));
        aliceResult.Should().NotBeNull();
        aliceResult!.Confidence.Should().BeApproximately(0.8, 1e-9);

        // Confidence reflects only Alice's +0.3 — Bob's attempt changed nothing.
        (await _repo.GetByIdAsync(id))!.Confidence.Should().BeApproximately(0.8, 1e-9);
    }

    // ── Cross-owner delete / merge / spatial denial (R1 isolation hardening) ──

    private async Task<string> SeedOwnedEntityAsync(string name, string? owner)
    {
        var id = $"entity-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Entity
        {
            EntityId = id, Name = name, Type = "Organization",
            Confidence = 0.7, OwnerId = owner, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task DeleteAsync_ForeignOwnerScope_DoesNotDeleteOtherOwnersEntity()
    {
        var id = await SeedOwnedEntityAsync("Alice private", "alice");

        (await _repo.DeleteAsync(id, MemoryScope.For("bob"))).Should().BeFalse();
        (await _repo.GetByIdAsync(id)).Should().NotBeNull("bob's scope must not delete alice's entity");

        (await _repo.DeleteAsync(id, MemoryScope.For("alice"))).Should().BeTrue();
        (await _repo.GetByIdAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_Scoped_DoesNotDeleteSharedEntity()
    {
        var id = await SeedOwnedEntityAsync("Shared", owner: null);

        (await _repo.DeleteAsync(id, MemoryScope.For("alice"))).Should().BeFalse("a scoped delete must not remove shared/global data");
        (await _repo.GetByIdAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task MergeEntitiesAsync_ForeignOwnerScope_DoesNotMergeAcrossOwners()
    {
        var aliceId = await SeedOwnedEntityAsync("AliceCo", "alice");
        var bobId = await SeedOwnedEntityAsync("BobCo", "bob");
        var bobBefore = await _repo.GetByIdAsync(bobId);

        // bob's scope must not be able to merge alice's entity into bob's.
        var merged = await _repo.MergeEntitiesAsync(aliceId, bobId, MemoryScope.For("bob"));

        merged.Should().BeFalse("a guarded cross-owner merge matches nothing and is a no-op");
        (await _repo.GetByIdAsync(aliceId)).Should().NotBeNull("the cross-owner merge must no-op");
        var bobAfter = await _repo.GetByIdAsync(bobId);
        bobAfter!.Aliases.Should().NotContain("AliceCo", "bob's entity must not absorb alice's name");
        // A guarded no-match merge is a TRUE no-op: it must NOT run the post-merge search-field refresh,
        // which would otherwise bump bob's updated_at — a scoped call silently writing another owner's node.
        bobAfter.UpdatedAtUtc.Should().Be(bobBefore!.UpdatedAtUtc,
            "a no-op merge must not touch bob's entity at all (no post-merge refresh)");
    }

    [Fact]
    public async Task MergeEntitiesAsync_SameOwner_ReturnsTrue_AndAbsorbsSourceName()
    {
        var targetId = await SeedOwnedEntityAsync("Alice Johnson", "alice");
        var sourceId = await SeedOwnedEntityAsync("A. Johnson", "alice");

        var merged = await _repo.MergeEntitiesAsync(sourceId, targetId, MemoryScope.For("alice"));

        merged.Should().BeTrue("both endpoints are alice's own entities, so the merge matches and runs");
        (await _repo.GetByIdAsync(targetId))!.Aliases.Should().Contain("A. Johnson",
            "the surviving target absorbs the source's name as an alias");
    }

    [Fact]
    public async Task SearchByLocationAsync_OwnerScoped_ExcludesOtherOwners()
    {
        const double lat = 51.5, lon = -0.12;
        var aliceId = await SeedOwnedEntityAsync("AliceSpot", "alice");
        var bobId = await SeedOwnedEntityAsync("BobSpot", "bob");
        var sharedId = await SeedOwnedEntityAsync("SharedSpot", owner: null);
        foreach (var id in new[] { aliceId, bobId, sharedId })
            await SetLocationAsync(id, lat, lon);

        var results = await _repo.SearchByLocationAsync(lat, lon, 5.0, 10, MemoryScope.For("alice"));
        var ids = results.Select(e => e.EntityId).ToList();

        ids.Should().Contain(aliceId).And.Contain(sharedId);
        ids.Should().NotContain(bobId, "bob's location is invisible to alice's scoped spatial search");
    }

    private Task SetLocationAsync(string id, double lat, double lon) =>
        _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "MATCH (e:Entity {id: $id}) SET e.location = point({latitude: $lat, longitude: $lon})",
                new { id, lat, lon });
        });

    [Fact]
    public async Task UpsertAsync_WithLatLon_PersistsLocation_AndRoundTrips()
    {
        const double lat = 48.8566, lon = 2.3522; // Paris
        var id = $"entity-{Guid.NewGuid():N}";
        var returned = await _repo.UpsertAsync(new Entity
        {
            EntityId = id, Name = "Paris HQ", Type = "Location",
            Confidence = 0.9, Latitude = lat, Longitude = lon, CreatedAtUtc = DateTimeOffset.UtcNow
        });

        // The RETURNED object (not just a later re-read) must carry the coordinates that were persisted —
        // the node is captured from the MERGE before the location is written, so this guards that regression.
        returned.Latitude.Should().BeApproximately(lat, 1e-6);
        returned.Longitude.Should().BeApproximately(lon, 1e-6);

        var read = await _repo.GetByIdAsync(id);
        read!.Latitude.Should().BeApproximately(lat, 1e-6);
        read.Longitude.Should().BeApproximately(lon, 1e-6);

        // And it is findable by the spatial search (proves the point() is well-formed, not just stored).
        var near = await _repo.SearchByLocationAsync(lat, lon, radiusKm: 1.0, limit: 10);
        near.Select(e => e.EntityId).Should().Contain(id);
    }

    [Fact]
    public async Task UpsertBatchAsync_WithLatLon_PersistsLocation_AndRoundTrips()
    {
        const double lat = 40.7128, lon = -74.0060; // New York
        var withLoc = $"entity-{Guid.NewGuid():N}";
        var withoutLoc = $"entity-{Guid.NewGuid():N}";
        var returned = await _repo.UpsertBatchAsync(new[]
        {
            new Entity { EntityId = withLoc, Name = "NYC Office", Type = "Location", Confidence = 0.9, Latitude = lat, Longitude = lon, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Entity { EntityId = withoutLoc, Name = "No Coords", Type = "Concept", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow },
        });

        // The RETURNED objects must carry coords (with-loc) / null (without-loc), matching persisted state.
        var returnedWithLoc = returned.Single(e => e.EntityId == withLoc);
        returnedWithLoc.Latitude.Should().BeApproximately(lat, 1e-6);
        returnedWithLoc.Longitude.Should().BeApproximately(lon, 1e-6);
        returned.Single(e => e.EntityId == withoutLoc).Latitude.Should().BeNull();

        var read = await _repo.GetByIdAsync(withLoc);
        read!.Latitude.Should().BeApproximately(lat, 1e-6);
        read.Longitude.Should().BeApproximately(lon, 1e-6);

        // Entities without coords stay null (no spurious location written).
        var readNoLoc = await _repo.GetByIdAsync(withoutLoc);
        readNoLoc!.Latitude.Should().BeNull();
        readNoLoc.Longitude.Should().BeNull();

        var near = await _repo.SearchByLocationAsync(lat, lon, radiusKm: 1.0, limit: 10);
        near.Select(e => e.EntityId).Should().Contain(withLoc).And.NotContain(withoutLoc);
    }

    [Fact]
    public async Task Entity_UpdatedAtUtc_RoundTrips_AfterReUpsert()
    {
        // updated_at is set ON MATCH (last-modified semantics): null on first create, populated after an
        // update. Verify it round-trips into the model once the entity is modified.
        var id = await SeedEntityAsync(0.5);

        var afterCreate = await _repo.GetByIdAsync(id);
        afterCreate!.UpdatedAtUtc.Should().BeNull("a freshly created, never-updated entity has no update time");

        await _repo.UpsertAsync(new Entity
        {
            EntityId = id, Name = "Feedback Subject (edited)", Type = "Concept",
            Confidence = 0.6, CreatedAtUtc = DateTimeOffset.UtcNow
        });

        var afterUpdate = await _repo.GetByIdAsync(id);
        afterUpdate!.UpdatedAtUtc.Should().NotBeNull("the second upsert hits ON MATCH and stamps updated_at");
    }

    [Fact]
    public async Task GetByNameAsync_FindsEntityByExactName()
    {
        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Acme Corporation",
            Type = "Organization",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(entity);

        var results = await _repo.GetByNameAsync("Acme Corporation");

        results.Should().NotBeEmpty();
        results.Should().Contain(e => e.EntityId == entity.EntityId);
    }

    [Fact]
    public async Task UpsertAsync_WithEmbedding_PersistsVector()
    {
        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Embedded Entity",
            Type = "Concept",
            Confidence = 0.7,
            Embedding = TestEmbedding,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(entity);

        var fetched = await _repo.GetByIdAsync(entity.EntityId);
        fetched.Should().NotBeNull();
        fetched!.Embedding.Should().NotBeNull();
        fetched.Embedding!.Length.Should().Be(TestEmbedding.Length);
    }

    [Fact]
    public async Task SearchByVectorAsync_ReturnsEntities_WhenEmbeddingMatches()
    {
        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Vector Search Target",
            Type = "Concept",
            Confidence = 0.85,
            Embedding = TestEmbedding,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(entity);

        var results = await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5);

        results.Should().NotBeEmpty();
        results[0].Entity.EntityId.Should().Be(entity.EntityId);
        results[0].Score.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public async Task UpsertBatchAsync_PersistsAllEntities()
    {
        var entities = new List<Entity>
        {
            new() { EntityId = $"e-{Guid.NewGuid():N}", Name = "Entity A", Type = "Person", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow },
            new() { EntityId = $"e-{Guid.NewGuid():N}", Name = "Entity B", Type = "Organization", Confidence = 0.8, CreatedAtUtc = DateTimeOffset.UtcNow },
            new() { EntityId = $"e-{Guid.NewGuid():N}", Name = "Entity C", Type = "Location", Confidence = 0.7, CreatedAtUtc = DateTimeOffset.UtcNow }
        };

        var results = await _repo.UpsertBatchAsync(entities);

        results.Should().HaveCount(3);
        results.Select(e => e.Name).Should().BeEquivalentTo(["Entity A", "Entity B", "Entity C"]);
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntity()
    {
        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "To Be Deleted",
            Type = "Concept",
            Confidence = 0.5,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(entity);

        await _repo.DeleteAsync(entity.EntityId);

        var result = await _repo.GetByIdAsync(entity.EntityId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_CreatesRelationship()
    {
        // Seed a conversation and message first
        var convRepo = new Neo4jConversationRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jConversationRepository>.Instance);
        var msgRepo = new Neo4jMessageRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jMessageRepository>.Instance);

        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await convRepo.UpsertAsync(conv);

        var msg = new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = conv.SessionId,
            Role = "user",
            Content = "Source message",
            TimestampUtc = DateTimeOffset.UtcNow
        };
        await msgRepo.AddAsync(msg);

        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Extracted Entity",
            Type = "Person",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(entity);

        // Act
        await _repo.CreateExtractedFromRelationshipAsync(entity.EntityId, msg.MessageId);

        // Verify with Cypher
        var txRunner = _fixture.TransactionRunner;
        var count = await txRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (e:Entity {id: $eid})-[:EXTRACTED_FROM]->(m:Message {id: $mid}) RETURN count(*) AS c",
                new { eid = entity.EntityId, mid = msg.MessageId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1);
    }

    [Fact]
    public async Task GetByTypeAsync_ReturnsOnlyEntitiesOfType()
    {
        var entityPerson = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Alice Person",
            Type = "Person",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var entityOrg = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Org Name",
            Type = "Organization",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(entityPerson);
        await _repo.UpsertAsync(entityOrg);

        var results = await _repo.GetByTypeAsync("Person");

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(e => e.Type.Should().Be("Person"));
        results.Should().Contain(e => e.EntityId == entityPerson.EntityId);
        results.Should().NotContain(e => e.EntityId == entityOrg.EntityId);
    }

    // ── R6-B: tombstoned entities must drop out of the resolution/dedup candidate sets ──

    [Fact]
    public async Task GetByTypeAsync_ExcludesInvalidatedEntities()
    {
        var live = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}", Name = "Live Person", Type = "Person",
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var tombstoned = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}", Name = "Tombstoned Person", Type = "Person",
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(live);
        await _repo.UpsertAsync(tombstoned);
        (await _repo.InvalidateAsync(tombstoned.EntityId)).Should().BeTrue();

        var results = await _repo.GetByTypeAsync("Person");

        results.Should().Contain(e => e.EntityId == live.EntityId);
        results.Should().NotContain(e => e.EntityId == tombstoned.EntityId,
            "a soft-invalidated entity must not be a resolution candidate (else a re-extracted entity merges into a tombstone)");
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_ExcludesInvalidatedCandidates()
    {
        var source = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}", Name = "Source", Type = "Concept",
            Confidence = 0.9, Embedding = TestEmbedding, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var liveDup = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}", Name = "Live Dup", Type = "Concept",
            Confidence = 0.9, Embedding = TestEmbedding, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var tombstonedDup = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}", Name = "Tombstoned Dup", Type = "Concept",
            Confidence = 0.9, Embedding = TestEmbedding, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(source);
        await _repo.UpsertAsync(liveDup);
        await _repo.UpsertAsync(tombstonedDup);
        (await _repo.InvalidateAsync(tombstonedDup.EntityId)).Should().BeTrue();

        var results = await _repo.FindSimilarByEmbeddingAsync(source.EntityId, minSimilarity: 0.0, limit: 10);

        var ids = results.Select(r => r.Entity.EntityId).ToList();
        ids.Should().Contain(liveDup.EntityId);
        ids.Should().NotContain(tombstonedDup.EntityId,
            "a soft-invalidated entity must not be presented as a live duplicate candidate");
    }
}
