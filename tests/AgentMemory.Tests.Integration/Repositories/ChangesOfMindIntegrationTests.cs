using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 36.4, on a real store: a new value finds the old one under any stored form of its relation, and a superseded
/// preference reads back as superseded.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ChangesOfMindIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jPreferenceRepository _preferences;

    public ChangesOfMindIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _preferences = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact F(string id, string predicate, string @object) => new()
    {
        FactId = id, Subject = "Nadia", Predicate = predicate, Object = @object, Confidence = 0.9,
        OwnerId = "owner-com", CreatedAtUtc = T0,
    };

    [Fact]
    public async Task A_new_employer_stated_as_works_for_finds_the_one_stored_as_works_at()
    {
        await _facts.UpsertAsync(F("old", "works at", "a shipping company"));
        await _facts.UpsertAsync(F("likes", "likes", "a shipping company"));
        var winner = await _facts.UpsertAsync(F("new", "works for", "a wind energy firm"));

        var candidates = await _facts.FindSupersededCandidatesAsync(
            winner.FactId, "Nadia", "works for", "a wind energy firm", MemoryScope.For("owner-com", includeShared: false));

        candidates.Select(f => f.FactId).Should().Equal(["old"], "every stored form of the relation, and only that relation");
    }

    [Fact]
    public async Task A_superseded_preference_reads_back_as_superseded()
    {
        Preference P(string id, string text) => new()
        {
            PreferenceId = id, Category = "music", PreferenceText = text, Confidence = 0.9, OwnerId = "owner-com", CreatedAtUtc = T0,
        };
        await _preferences.UpsertAsync(P("radiohead", "likes Radiohead"));
        await _preferences.UpsertAsync(P("arcade", "likes Arcade Fire"));

        await _preferences.SupersedeAsync("radiohead", "arcade", MemoryScope.For("owner-com", includeShared: false));

        var read = await _preferences.GetByCategoryAsync("music", MemoryScope.For("owner-com", includeShared: false));
        read.Single(p => p.PreferenceId == "radiohead").InvalidatedAtUtc.Should().NotBeNull();
        read.Single(p => p.PreferenceId == "arcade").InvalidatedAtUtc.Should().BeNull();
    }

    private static readonly MemoryScope Owner = MemoryScope.For("owner-com", includeShared: false);

    /// <summary>Review round 8: only a value that holds now is replaced; history and plans are not.</summary>
    [Fact]
    public async Task A_value_that_has_ended_or_not_begun_is_no_candidate()
    {
        await _facts.UpsertAsync(F("history", "lives in", "Berlin") with { ValidUntil = new DateTimeOffset(2019, 6, 1, 0, 0, 0, TimeSpan.Zero) });
        await _facts.UpsertAsync(F("plan", "lives in", "Bergen") with { ValidFrom = DateTimeOffset.UtcNow.AddDays(60) });
        await _facts.UpsertAsync(F("now", "lives in", "Lisbon"));
        var winner = await _facts.UpsertAsync(F("new", "lives in", "Oslo"));

        var candidates = await _facts.FindSupersededCandidatesAsync(winner.FactId, "Nadia", "lives in", "Oslo", Owner);

        candidates.Select(f => f.FactId).Should().Equal(["now"]);
    }
}
