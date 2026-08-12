using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The salience counter behind R7, verified in the database rather than in the Cypher text.
/// </summary>
/// <remarks>
/// <para>
/// <c>mention_count</c> must record how often <b>the world</b> asserted a fact. Unit tests can prove
/// the query says so; only a live database proves the MERGE actually increments on re-assertion and
/// not on, say, every write of any fact about the same subject.
/// </para>
/// <para>
/// The failure this guards against is silent and slow: a counter that never moves makes the reranker
/// a no-op that looks enabled, and one that moves on the wrong event makes it rank on noise.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class MentionCountIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    public MentionCountIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Fact> AssertAsync(string subject, string predicate, string @object) =>
        _facts.UpsertAsync(new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = subject,
            Predicate = predicate,
            Object = @object,
            Confidence = 0.9,
            OwnerId = "alice",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

    private Task<int> MentionsOfAsync(string factId) =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                FactQueries.MentionCounts, new { candidateIds = new[] { factId } });
            var records = await cursor.ToListAsync();
            // Fully qualified: FluentAssertions and the Neo4j driver both offer As<T>, and the
            // ambiguity is a compile error rather than a silent wrong conversion (the precedent is
            // ReasoningTracePruneIntegrationTests).
            return records.Count == 0
                ? 0
                : global::Neo4j.Driver.ValueExtensions.As<int>(records[0]["mentions"]);
        });

    [Fact]
    public async Task AFirstAssertionCountsOnce()
    {
        var stored = await AssertAsync("user", "lives in", "Zurich");

        (await MentionsOfAsync(stored.FactId)).Should().Be(1);
    }

    [Fact]
    public async Task ReAssertingTheSameFactIncrements()
    {
        // THE behaviour: the world said it again. The triple MERGEs to the same node, so this is the
        // one moment that distinguishes a fact the conversation keeps returning to from one mentioned
        // in passing.
        var stored = await AssertAsync("user", "lives in", "Zurich");
        await AssertAsync("user", "lives in", "Zurich");
        await AssertAsync("user", "lives in", "Zurich");

        (await MentionsOfAsync(stored.FactId)).Should().Be(3);
    }

    [Fact]
    public async Task ADifferentFactAboutTheSameSubjectDoesNotIncrement()
    {
        // Salience is per fact, not per subject. Counting every write about "user" would make one
        // chatty subject's facts all look equally central.
        var lives = await AssertAsync("user", "lives in", "Zurich");
        await AssertAsync("user", "works at", "Acme");
        await AssertAsync("user", "likes", "tea");

        (await MentionsOfAsync(lives.FactId)).Should().Be(1);
    }

    [Fact]
    public async Task ADifferentObjectIsADifferentFact()
    {
        // "lives in Zurich" and "lives in Basel" are separate assertions, not one fact asserted twice
        // -- the MERGE key includes the object precisely so contradiction stays visible.
        var zurich = await AssertAsync("user", "lives in", "Zurich");
        var basel = await AssertAsync("user", "lives in", "Basel");

        (await MentionsOfAsync(zurich.FactId)).Should().Be(1);
        (await MentionsOfAsync(basel.FactId)).Should().Be(1);
    }

    [Fact]
    public async Task ReadingAFactNeverIncrementsIt()
    {
        // The loop this must not close. Retrieval must leave the counter alone, or ranking starts
        // reinforcing whatever already ranks highly on evidence it generated itself.
        var stored = await AssertAsync("user", "lives in", "Zurich");

        for (var i = 0; i < 5; i++)
            await _facts.GetByIdAsync(stored.FactId);
        await _facts.GetBySubjectAsync("user", MemoryScope.For("alice"));

        (await MentionsOfAsync(stored.FactId)).Should().Be(1,
            "reads are not mentions -- counting them would make the ranking self-reinforcing");
    }

    [Fact]
    public async Task AnotherOwnersAssertionCountsSeparately()
    {
        // The MERGE key includes owner_key, so two tenants asserting the same sentence are two facts.
        // A shared counter would leak one tenant's conversational emphasis into another's ranking.
        var alice = await AssertAsync("user", "lives in", "Zurich");
        await _facts.UpsertAsync(new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "user",
            Predicate = "lives in",
            Object = "Zurich",
            Confidence = 0.9,
            OwnerId = "bob",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        (await MentionsOfAsync(alice.FactId)).Should().Be(1);
    }
}
