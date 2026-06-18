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
    public async Task FindDuplicate_DoesNotMatchInvalidatedNodes_SoReAssertedKnowledgeIsNotLost()
    {
        // A soft-invalidated (decayed/superseded) node must NOT be a dedup target — otherwise re-asserting
        // the same knowledge dedups onto the dead node (which stays invisible to live recall) and is
        // silently lost instead of being re-created as a fresh live node.
        var factId = $"f-{Guid.NewGuid():N}";
        await _facts.UpsertAsync(new Fact { FactId = factId, Subject = "Alice", Predicate = "works_at", Object = "Acme", OwnerId = "alice", Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow });
        (await _facts.InvalidateAsync(factId, scope: null)).Should().BeTrue();
        (await _facts.FindDuplicateAsync("Alice", "works_at", E, "alice", threshold: 0.95))
            .Should().BeNull("dedup must only match live facts, never a soft-invalidated one");

        var prefId = $"p-{Guid.NewGuid():N}";
        await _prefs.UpsertAsync(new Preference { PreferenceId = prefId, Category = "style", PreferenceText = "dark mode", OwnerId = "alice", Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow });
        (await _prefs.InvalidateAsync(prefId, scope: null)).Should().BeTrue();
        (await _prefs.FindDuplicateAsync("style", E, "alice", threshold: 0.95))
            .Should().BeNull("dedup must only match live preferences, never a soft-invalidated one");
    }

    [Fact]
    public async Task FactMarkDeduplicated_BumpsConfidence()
    {
        var f = new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "works_at", Object = "Acme", OwnerId = null, Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow };
        await _facts.UpsertAsync(f);

        var updated = await _facts.MarkDeduplicatedAsync(f.FactId, 0.85);

        updated.Should().NotBeNull("the node still exists, so the reinforce returns it");
        updated!.Confidence.Should().Be(0.85);
        (await _facts.GetByIdAsync(f.FactId))!.Confidence.Should().Be(0.85);
    }

    [Fact]
    public async Task FactMarkDeduplicated_NonexistentId_ReturnsNull_DoesNotThrow()
    {
        // The dedup target can be concurrently hard-deleted between find and reinforce; the reinforce must
        // return null (empty result) rather than throwing, so the caller can fall through to create.
        var result = await _facts.MarkDeduplicatedAsync($"f-does-not-exist-{Guid.NewGuid():N}", 0.9);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PreferenceMarkDeduplicated_NonexistentId_ReturnsNull_DoesNotThrow()
    {
        var result = await _prefs.MarkDeduplicatedAsync($"p-does-not-exist-{Guid.NewGuid():N}", 0.9);

        result.Should().BeNull();
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
