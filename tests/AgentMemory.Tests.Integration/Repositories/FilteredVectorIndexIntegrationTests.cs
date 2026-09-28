using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// G-16: owner-filtered vector indexes (Neo4j 2026.x). On a newer server, bootstrap creates them and owner-scoped fact
/// and entity recall filters inside the index, so an owner is never crowded out by others' rows. On 5.26 the option is
/// refused at bootstrap with a message naming it. Run on a newer server with AGENTMEMORY_TEST_NEO4J_IMAGE=neo4j:2026.02.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class FilteredVectorIndexIntegrationTests : IAsyncLifetime
{
    private const string Owner = "filtered-owner";
    private readonly Neo4jIntegrationFixture _fixture;

    public FilteredVectorIndexIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public async Task DisposeAsync()
    {
        // The filtered indexes are this test's own: drop them so the other tests see the schema they expect.
        await using var session = _fixture.Driver.AsyncSession();
        foreach (var name in new[] { "fact_embedding_owner_idx", "entity_embedding_owner_idx" })
            await session.RunAsync($"DROP INDEX {name} IF EXISTS");
    }

    private IOptions<Neo4jOptions> Options(bool filtered) => Microsoft.Extensions.Options.Options.Create(new Neo4jOptions
    {
        EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions,
        FilteredVectorIndexes = filtered,
    });

    private static float[] Vector(int hot)
    {
        var v = new float[Neo4jIntegrationFixture.TestEmbeddingDimensions];
        v[hot] = 1f;
        return v;
    }

    [Fact]
    public async Task On_an_older_server_the_option_is_refused_by_name()
    {
        if (await _fixture.ServerMajorAsync() >= 2026) return;
        var bootstrapper = new SchemaBootstrapper(_fixture.TransactionRunner, Options(filtered: true), NullLogger<SchemaBootstrapper>.Instance);

        var act = () => bootstrapper.BootstrapAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("FilteredVectorIndexes");
    }

    [Fact]
    public async Task On_a_newer_server_an_owner_is_found_through_the_filtered_index_however_crowded()
    {
        if (await _fixture.ServerMajorAsync() < 2026) return;
        await new SchemaBootstrapper(_fixture.TransactionRunner, Options(filtered: true), NullLogger<SchemaBootstrapper>.Instance)
            .BootstrapAsync();
        await using (var session = _fixture.Driver.AsyncSession())
        {
            await session.RunAsync("CALL db.awaitIndexes(60)");
            var cursor = await session.RunAsync(
                "SHOW VECTOR INDEXES YIELD name, state WHERE name IN ['fact_embedding_owner_idx', 'entity_embedding_owner_idx'] RETURN name, state");
            (await cursor.ToListAsync()).Select(r => ValueExtensions.As<string>(r["state"])).Should().Equal(["ONLINE", "ONLINE"]);
        }

        var facts = new Neo4jFactRepository(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance, neo4jOptions: Options(filtered: true));
        var now = DateTimeOffset.UtcNow;
        // 200 of another owner's facts closer to the query than this owner's only fact: a global top-K sees only them.
        await facts.UpsertBatchAsync([.. Enumerable.Range(0, 200).Select(i => new Fact
        {
            FactId = $"crowd-{i}", Subject = $"Crowd {i}", Predicate = "likes", Object = "jazz", Confidence = 0.9,
            OwnerId = "someone-else", CreatedAtUtc = now, Embedding = Vector(0),
        })]);
        await facts.UpsertAsync(new Fact
        {
            FactId = "mine", Subject = "Priya", Predicate = "likes", Object = "bonsai", Confidence = 0.9,
            OwnerId = Owner, CreatedAtUtc = now, Embedding = Vector(1),
        });
        await facts.UpsertAsync(new Fact
        {
            FactId = "shared", Subject = "Alice", Predicate = "follows", Object = "the White Rabbit", Confidence = 0.9,
            CreatedAtUtc = now, Embedding = Vector(1),
        });
        await using (var session = _fixture.Driver.AsyncSession()) await session.RunAsync("CALL db.awaitIndexes(60)");

        string? plan = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.OperationName == "memory.recall.fact_vector") plan = a.GetTagItem("memory.vector.owner_first") as string;
            },
        };
        ActivitySource.AddActivityListener(listener);

        var own = await facts.SearchByVectorAsync(Vector(0), limit: 1, scope: MemoryScope.For(Owner, includeShared: false));
        var withShared = await facts.SearchByVectorAsync(Vector(1), limit: 5, scope: MemoryScope.For(Owner, includeShared: true));

        own.Select(r => r.Fact.FactId).Should().Equal(["mine"], "the owner's one fact, though 200 foreign ones are closer");
        withShared.Select(r => r.Fact.FactId).Should().BeEquivalentTo(["mine", "shared"], "own and shared, merged by score");
        plan.Should().Be("FilteredIndex", "the search went through the owner-filtered index");
    }
}
