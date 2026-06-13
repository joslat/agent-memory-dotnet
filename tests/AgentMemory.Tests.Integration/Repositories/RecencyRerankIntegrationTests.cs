using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// D1 recency re-ranker behaviour against live Neo4j. With the re-ranker OFF (parity default) results
/// rank by raw vector similarity; with it ON, a fresher-but-less-similar memory outranks an older-but-
/// more-similar one. This proves the blend actually reorders real recall results — not just the Cypher
/// text (which the unit tests cover). The blend is schema-neutral: the same seeded nodes are queried by
/// both repositories; only the ranking options differ.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class RecencyRerankIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _parity;    // re-ranker off (today's behaviour)
    private readonly Neo4jFactRepository _enhanced;  // re-ranker on (recency weight 0.9)

    // Query == the old fact's embedding (cosine 1.0); the fresh fact is less similar but recent.
    private static readonly float[] Query    = [1f, 0f, 0f, 0f];
    private static readonly float[] OldHigh  = [1f, 0f, 0f, 0f];
    private static readonly float[] FreshLow = [0.8f, 0.6f, 0f, 0f];

    public RecencyRerankIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _parity = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _enhanced = new Neo4jFactRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jFactRepository>.Instance,
            Options.Create(new MemoryRankingOptions { RecencyWeight = 0.9 }),
            Options.Create(new MemoryDecayOptions()));
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RecencyRerank_OffRanksBySimilarity_OnPromotesFresherMemory()
    {
        var now = DateTimeOffset.UtcNow;

        // Most-similar (cosine 1.0) but stale — last activity 200 days ago.
        await _parity.UpsertAsync(new Fact
        {
            FactId = "f-old", Subject = "old", Predicate = "is", Object = "stale",
            Confidence = 0.9, Embedding = OldHigh, OwnerId = null, CreatedAtUtc = now.AddDays(-200),
        });
        // Less-similar but fresh — created now.
        await _parity.UpsertAsync(new Fact
        {
            FactId = "f-fresh", Subject = "fresh", Predicate = "is", Object = "recent",
            Confidence = 0.9, Embedding = FreshLow, OwnerId = null, CreatedAtUtc = now,
        });

        var off = await _parity.SearchByVectorAsync(Query, limit: 5);
        var on = await _enhanced.SearchByVectorAsync(Query, limit: 5);

        off.Should().HaveCount(2);
        on.Should().HaveCount(2);
        off[0].Fact.FactId.Should().Be("f-old",
            "without re-ranking the most-similar memory ranks first");
        on[0].Fact.FactId.Should().Be("f-fresh",
            "with recency re-ranking the fresher memory is promoted over the stale top-similarity one");
    }
}
