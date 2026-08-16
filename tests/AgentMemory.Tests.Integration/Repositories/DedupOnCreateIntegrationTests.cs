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

    /// <summary>
    /// A dedup hit is the world re-asserting a fact, so it must move <c>mention_count</c> — the same
    /// counter the other three fact write paths maintain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was broken. <c>MarkDeduplicated</c> bumped confidence and nothing else, so with
    /// <c>LongTerm.DeduplicateOnCreate</c> at its shipped default of <c>true</c>, a re-assert through
    /// <c>AddFactAsync</c> — including a byte-identical triple, which trivially clears the similarity
    /// threshold — never reached the triple MERGE's <c>ON MATCH SET f.mention_count = … + 1</c>.
    /// The counter stayed at 1 forever on the single-add API.
    /// </para>
    /// <para>
    /// <b>Why it mattered rather than being cosmetic.</b> The working-memory tier admits a fact only at
    /// <c>MinFactMentionCount</c> (default 2), so a fact added through the direct API could never become
    /// stable however often it was re-asserted; the block stayed empty and said nothing about why.
    /// <see cref="AgentMemory.Neo4j.Services.MentionFrequencyReranker"/> lost the same signal.
    /// <c>UpsertBatch</c>'s own comment states the invariant this violated: the counter must not
    /// "depend on which write path ran".
    /// </para>
    /// <para>
    /// Found while building the LangGraph demo adapter, whose writes go through exactly this path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FactMarkDeduplicated_IncrementsMentionCount_LikeEveryOtherWritePath()
    {
        var f = new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "works_at", Object = "Acme", OwnerId = "alice", Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow };
        await _facts.UpsertAsync(f);
        (await MentionCountAsync(f.FactId)).Should().Be(1, "ON CREATE seeds the counter at 1");

        await _facts.MarkDeduplicatedAsync(f.FactId, 0.85);
        (await MentionCountAsync(f.FactId)).Should().Be(2, "a dedup hit is one more assertion by the world");

        await _facts.MarkDeduplicatedAsync(f.FactId, 0.9);
        (await MentionCountAsync(f.FactId)).Should().Be(3, "and it keeps counting");
    }

    /// <summary>
    /// The counter must reach the same value whichever write path re-asserted the fact — the invariant
    /// <c>UpsertBatch</c> states and the dedup path used to break.
    /// </summary>
    [Fact]
    public async Task MentionCount_DoesNotDependOnWhichWritePathRan()
    {
        var viaUpsert = new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "likes", Object = "coffee", OwnerId = "alice", Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow };
        await _facts.UpsertAsync(viaUpsert);
        await _facts.UpsertAsync(viaUpsert);   // same triple again: the ON MATCH path

        var viaDedup = new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Bob", Predicate = "likes", Object = "tea", OwnerId = "bob", Confidence = 0.8, Embedding = E, CreatedAtUtc = DateTimeOffset.UtcNow };
        await _facts.UpsertAsync(viaDedup);
        await _facts.MarkDeduplicatedAsync(viaDedup.FactId, 0.85);   // the dedup path

        (await MentionCountAsync(viaDedup.FactId))
            .Should().Be(await MentionCountAsync(viaUpsert.FactId),
                "two assertions is two assertions, whichever path carried them");
    }

    private Task<int> MentionCountAsync(string factId) =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $id}) RETURN coalesce(f.mention_count, 0) AS count",
                new { id = factId });
            var records = await cursor.ToListAsync();
            return records.Count == 0
                ? -1
                : global::Neo4j.Driver.ValueExtensions.As<int>(records[0]["count"]);
        });

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
