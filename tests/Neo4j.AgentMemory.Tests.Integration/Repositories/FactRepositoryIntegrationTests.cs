using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class FactRepositoryIntegrationTests
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _repo;

    private static readonly float[] TestEmbedding = [0.3f, 0.1f, 0.4f, 0.2f];
    private static readonly float[] QueryEmbedding = [0.3f, 0.1f, 0.4f, 0.2f];

    public FactRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jFactRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertAsync_CreatesFact_WithSubjectPredicateObject()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Alice",
            Predicate = "works_at",
            Object = "Acme Corp",
            Confidence = 0.9,
            CreatedAtUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await _repo.UpsertAsync(fact);

        result.FactId.Should().Be(fact.FactId);
        result.Subject.Should().Be("Alice");
        result.Predicate.Should().Be("works_at");
        result.Object.Should().Be("Acme Corp");
        result.Confidence.Should().Be(0.9);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsFact_WhenExists()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Bob",
            Predicate = "likes",
            Object = "Python",
            Confidence = 0.75,
            CreatedAtUtc = new DateTimeOffset(2025, 3, 10, 8, 0, 0, TimeSpan.Zero)
        };
        await _repo.UpsertAsync(fact);

        var result = await _repo.GetByIdAsync(fact.FactId);

        result.Should().NotBeNull();
        result!.FactId.Should().Be(fact.FactId);
        result.Subject.Should().Be("Bob");
        result.Predicate.Should().Be("likes");
        result.Object.Should().Be("Python");
        result.CreatedAtUtc.Should().BeCloseTo(fact.CreatedAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync("fact-does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBySubjectAsync_ReturnsFactsForSubject()
    {
        var subject = $"Subject-{Guid.NewGuid():N}";
        var fact1 = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = subject,
            Predicate = "works_at",
            Object = "Company A",
            Confidence = 0.8,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var fact2 = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = subject,
            Predicate = "lives_in",
            Object = "City B",
            Confidence = 0.7,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var other = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Someone Else",
            Predicate = "likes",
            Object = "Coffee",
            Confidence = 0.6,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await _repo.UpsertAsync(fact1);
        await _repo.UpsertAsync(fact2);
        await _repo.UpsertAsync(other);

        var results = await _repo.GetBySubjectAsync(subject);

        results.Should().HaveCount(2);
        results.Select(f => f.FactId).Should().BeEquivalentTo([fact1.FactId, fact2.FactId]);
    }

    [Fact]
    public async Task FindByTripleAsync_FindsFactBySPO_CaseInsensitive()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Charlie",
            Predicate = "knows",
            Object = "Diana",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        // Case-insensitive lookup
        var result = await _repo.FindByTripleAsync("CHARLIE", "KNOWS", "DIANA");

        result.Should().NotBeNull();
        result!.FactId.Should().Be(fact.FactId);
    }

    [Fact]
    public async Task FindByTripleAsync_ReturnsNull_WhenNoMatch()
    {
        var result = await _repo.FindByTripleAsync("Nobody", "does", "nothing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchByVectorAsync_ReturnsFacts_WhenEmbeddingMatches()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Eve",
            Predicate = "prefers",
            Object = "dark mode",
            Confidence = 0.85,
            Embedding = TestEmbedding,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        var results = await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5);

        results.Should().NotBeEmpty();
        results[0].Fact.FactId.Should().Be(fact.FactId);
        results[0].Score.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFact()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Temp",
            Predicate = "to_be",
            Object = "deleted",
            Confidence = 0.5,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        var deleted = await _repo.DeleteAsync(fact.FactId);

        deleted.Should().BeTrue();
        var fetched = await _repo.GetByIdAsync(fact.FactId);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task CreateAboutRelationshipAsync_LinksFactToEntity()
    {
        var entityRepo = new Neo4jEntityRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jEntityRepository>.Instance);

        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Frank",
            Type = "Person",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await entityRepo.UpsertAsync(entity);

        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Frank",
            Predicate = "is_a",
            Object = "developer",
            Confidence = 0.8,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        await _repo.CreateAboutRelationshipAsync(fact.FactId, entity.EntityId);

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $fid})-[:ABOUT]->(e:Entity {id: $eid}) RETURN count(*) AS c",
                new { fid = fact.FactId, eid = entity.EntityId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1);
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_LinksFactToMessage()
    {
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
            Content = "Source for fact",
            TimestampUtc = DateTimeOffset.UtcNow
        };
        await msgRepo.AddAsync(msg);

        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Grace",
            Predicate = "uses",
            Object = "Neo4j",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        await _repo.CreateExtractedFromRelationshipAsync(fact.FactId, msg.MessageId);

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $fid})-[:EXTRACTED_FROM]->(m:Message {id: $mid}) RETURN count(*) AS c",
                new { fid = fact.FactId, mid = msg.MessageId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1);
    }
}
