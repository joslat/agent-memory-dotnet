using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Dedup-on-create query coverage (PR#97): <c>FindDuplicateAsync</c> matches a same-subject+predicate /
/// same-category near-duplicate within the same owner above the similarity threshold, and
/// <c>MarkDeduplicatedAsync</c> reinforces (bumps) the existing node's confidence.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class DedupOnCreateIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jPreferenceRepository _prefs;

    private static readonly float[] E = [0.3f, 0.1f, 0.4f, 0.2f];
    private static readonly float[] Orthogonal = [0.0f, 0.0f, 0.0f, 1.0f];

    public DedupOnCreateIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _prefs = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FactFindDuplicate_MatchesSameSubjectPredicate_NotDifferentOnes()
    {
        await _facts.UpsertAsync(new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "works_at", Object = "Acme", OwnerId = "alice", Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow });

        var hit = await _facts.FindDuplicateAsync("Alice", "works_at", E, "alice", threshold: 0.95);
        var diffPredicate = await _facts.FindDuplicateAsync("Alice", "lives_in", E, "alice", threshold: 0.95);
        var diffOwner = await _facts.FindDuplicateAsync("Alice", "works_at", E, "bob", threshold: 0.95);
        var lowSim = await _facts.FindDuplicateAsync("Alice", "works_at", Orthogonal, "alice", threshold: 0.95);

        hit.Should().NotBeNull();
        diffPredicate.Should().BeNull();   // same embedding, different predicate
        diffOwner.Should().BeNull();        // same triple, different owner
        lowSim.Should().BeNull();           // same triple+owner, dissimilar embedding
    }

    [Fact]
    public async Task FactMarkDeduplicated_BumpsConfidence()
    {
        var f = new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "works_at", Object = "Acme", OwnerId = null, Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow };
        await _facts.UpsertAsync(f);

        var updated = await _facts.MarkDeduplicatedAsync(f.FactId, 0.85);

        updated.Confidence.Should().Be(0.85);
        (await _facts.GetByIdAsync(f.FactId))!.Confidence.Should().Be(0.85);
    }

    [Fact]
    public async Task PreferenceFindDuplicate_MatchesSameCategoryAndOwner_NotOthers()
    {
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "dark mode", OwnerId = "alice", Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow });

        var hit = await _prefs.FindDuplicateAsync("style", E, "alice", threshold: 0.95);
        var diffCategory = await _prefs.FindDuplicateAsync("language", E, "alice", threshold: 0.95);
        var diffOwner = await _prefs.FindDuplicateAsync("style", E, "bob", threshold: 0.95);

        hit.Should().NotBeNull();
        diffCategory.Should().BeNull();
        diffOwner.Should().BeNull();
    }

    [Fact]
    public async Task PreferenceFindDuplicate_SharedOwner_MatchesOnlyShared()
    {
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "company palette", OwnerId = null, Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow });

        var sharedHit = await _prefs.FindDuplicateAsync("style", E, ownerId: null, threshold: 0.95);
        var ownedMiss = await _prefs.FindDuplicateAsync("style", E, ownerId: "alice", threshold: 0.95);

        sharedHit.Should().NotBeNull();
        ownedMiss.Should().BeNull(); // a shared preference is not a dedup target for an owned add
    }
}
