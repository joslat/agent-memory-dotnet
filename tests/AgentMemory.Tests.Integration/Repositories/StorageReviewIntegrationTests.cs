using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>RVW-REST-STORAGE: the review of main's storage findings, on a real store.</summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class StorageReviewIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 3, 30, 0, TimeSpan.Zero);
    private readonly Neo4jIntegrationFixture _fixture;

    public StorageReviewIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Neo4jFactRepository Facts => new(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);

    private static Fact Paris(string id) => new()
    {
        FactId = id, Subject = "Alice", Predicate = "lives in", Object = "Paris", Confidence = 0.9, CreatedAtUtc = At, OwnerId = "alice",
    };

    // storage-3: decay soft-prunes with invalidated_reason = 'decay'; a restatement revived the fact but kept the reason,
    // so a later forget or plain supersede read as "decayed" and legible forgetting said the system "used to know" it.
    [Theory]
    [InlineData("single")]
    [InlineData("batch")]
    [InlineData("fused")]
    public async Task A_restated_decayed_fact_is_revived_without_its_decay_reason(string path)
    {
        await Facts.UpsertAsync(Paris("f-1"));
        await RunAsync("MATCH (f:Fact {id: 'f-1'}) SET f.invalidated_at = datetime(), f.invalidated_reason = 'decay'");

        switch (path)
        {
            case "single": await Facts.UpsertAsync(Paris("f-2")); break;
            case "batch": await Facts.UpsertBatchAsync([Paris("f-2"), Paris("f-3") with { Object = "Rome" }]); break;
            default: await Facts.UpsertFusedBatchAsync([Paris("f-2")]); break;
        }

        (await Scalar<long>("MATCH (f:Fact {id: 'f-1'}) WHERE f.invalidated_at IS NULL AND f.invalidated_reason IS NULL RETURN count(f)"))
            .Should().Be(1, "the restatement revived the fact, and it is no longer a decayed one");
    }

    // storage-5: the store-aware writer is shown the live preferences that mention the people a turn names; the query read
    // `preference_text`, which no preference has, and always found none.
    [Fact]
    public async Task A_preference_that_names_a_person_is_found_by_that_name()
    {
        var preferences = new Neo4jPreferenceRepository(_fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
        await preferences.UpsertAsync(new Preference
        {
            PreferenceId = "p-1", Category = "social", PreferenceText = "Lunch with Priya on Fridays", Confidence = 0.9, CreatedAtUtc = At,
            OwnerId = "alice",
        });

        var found = await preferences.FindMentioningAsync(["Priya"], MemoryScope.For("alice"), limit: 10);

        found.Select(p => p.PreferenceId).Should().Equal("p-1");
    }

    private async Task RunAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }

    private async Task<T> Scalar<T>(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher);
        return ValueExtensions.As<T>((await cursor.SingleAsync())[0]);
    }
}
