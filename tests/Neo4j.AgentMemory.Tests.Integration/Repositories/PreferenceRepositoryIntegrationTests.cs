using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class PreferenceRepositoryIntegrationTests
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jPreferenceRepository _repo;

    private static readonly float[] TestEmbedding = [0.2f, 0.4f, 0.1f, 0.3f];
    private static readonly float[] QueryEmbedding = [0.2f, 0.4f, 0.1f, 0.3f];

    public PreferenceRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jPreferenceRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jPreferenceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertAsync_CreatesPreference_WithRequiredProperties()
    {
        var pref = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = "communication",
            PreferenceText = "Prefers concise bullet-pointed answers",
            Context = "technical discussions",
            Confidence = 0.9,
            CreatedAtUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await _repo.UpsertAsync(pref);

        result.PreferenceId.Should().Be(pref.PreferenceId);
        result.Category.Should().Be("communication");
        result.PreferenceText.Should().Be("Prefers concise bullet-pointed answers");
        result.Context.Should().Be("technical discussions");
        result.Confidence.Should().Be(0.9);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsPreference_WhenExists()
    {
        var pref = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = "style",
            PreferenceText = "Prefers formal language",
            Confidence = 0.8,
            CreatedAtUtc = new DateTimeOffset(2025, 2, 15, 10, 0, 0, TimeSpan.Zero)
        };
        await _repo.UpsertAsync(pref);

        var result = await _repo.GetByIdAsync(pref.PreferenceId);

        result.Should().NotBeNull();
        result!.PreferenceId.Should().Be(pref.PreferenceId);
        result.Category.Should().Be("style");
        result.PreferenceText.Should().Be("Prefers formal language");
        result.CreatedAtUtc.Should().BeCloseTo(pref.CreatedAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync("pref-does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesPreference()
    {
        var pref = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = "misc",
            PreferenceText = "To be deleted",
            Confidence = 0.5,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(pref);

        await _repo.DeleteAsync(pref.PreferenceId);

        var result = await _repo.GetByIdAsync(pref.PreferenceId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByCategoryAsync_ReturnsPreferencesForCategory()
    {
        var category = $"cat-{Guid.NewGuid():N}";
        var pref1 = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = category,
            PreferenceText = "Preference one",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var pref2 = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = category,
            PreferenceText = "Preference two",
            Confidence = 0.8,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var other = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = "other-category",
            PreferenceText = "Different category",
            Confidence = 0.7,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await _repo.UpsertAsync(pref1);
        await _repo.UpsertAsync(pref2);
        await _repo.UpsertAsync(other);

        var results = await _repo.GetByCategoryAsync(category);

        results.Should().HaveCount(2);
        results.Select(p => p.PreferenceId).Should().BeEquivalentTo([pref1.PreferenceId, pref2.PreferenceId]);
    }

    [Fact]
    public async Task CreateAboutRelationshipAsync_LinksPreferenceToEntity()
    {
        var entityRepo = new Neo4jEntityRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jEntityRepository>.Instance);

        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Product X",
            Type = "Product",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await entityRepo.UpsertAsync(entity);

        var pref = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = "product",
            PreferenceText = "Prefers Product X over alternatives",
            Confidence = 0.85,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(pref);

        await _repo.CreateAboutRelationshipAsync(pref.PreferenceId, entity.EntityId);

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (p:Preference {id: $pid})-[:ABOUT]->(e:Entity {id: $eid}) RETURN count(*) AS c",
                new { pid = pref.PreferenceId, eid = entity.EntityId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1);
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_LinksPreferenceToMessage()
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
            Content = "I love dark mode",
            TimestampUtc = DateTimeOffset.UtcNow
        };
        await msgRepo.AddAsync(msg);

        var pref = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = "ui",
            PreferenceText = "Prefers dark mode",
            Confidence = 0.95,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(pref);

        await _repo.CreateExtractedFromRelationshipAsync(pref.PreferenceId, msg.MessageId);

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (p:Preference {id: $pid})-[:EXTRACTED_FROM]->(m:Message {id: $mid}) RETURN count(*) AS c",
                new { pid = pref.PreferenceId, mid = msg.MessageId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1);
    }

    [Fact]
    public async Task SearchByVectorAsync_ReturnsPreferences_WhenEmbeddingMatches()
    {
        var pref = new Preference
        {
            PreferenceId = $"pref-{Guid.NewGuid():N}",
            Category = "search",
            PreferenceText = "Vector search target preference",
            Confidence = 0.8,
            Embedding = TestEmbedding,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(pref);

        var results = await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5);

        results.Should().NotBeEmpty();
        results[0].Preference.PreferenceId.Should().Be(pref.PreferenceId);
        results[0].Score.Should().BeGreaterThan(0.0);
    }
}
