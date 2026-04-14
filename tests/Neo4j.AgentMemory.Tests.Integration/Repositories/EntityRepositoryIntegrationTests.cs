using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class EntityRepositoryIntegrationTests
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
}
