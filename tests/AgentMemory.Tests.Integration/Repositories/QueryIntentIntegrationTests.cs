using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// D3 query-intent presets against live Neo4j, driven by the ambient ranking context. The same seeded
/// facts are reranked differently per request: <c>Latest</c> promotes a fresh memory; <c>Analog</c> (zero
/// recency) keeps the most structurally/semantically similar one on top even when the configured ranking is
/// recency-heavy — i.e. an old-but-relevant precedent is not buried. This exercises the exact wiring the
/// assembler uses (publish a per-request <see cref="MemoryRankingOptions"/>; the repository reads it).
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class QueryIntentIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly DefaultMemoryRankingContext _ctx = new();
    private readonly Neo4jFactRepository _facts;

    private static readonly float[] Query    = [1f, 0f, 0f, 0f];
    private static readonly float[] OldHigh  = [1f, 0f, 0f, 0f];
    private static readonly float[] FreshLow = [0.8f, 0.6f, 0f, 0f];

    public QueryIntentIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance,
            Options.Create(new MemoryRankingOptions()),   // configured: parity (off)
            Options.Create(new MemoryDecayOptions()),
            _ctx);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Intent_LatestPromotesFresh_AnalogKeepsMostSimilar()
    {
        var now = DateTimeOffset.UtcNow;
        await _facts.UpsertAsync(new Fact
        {
            FactId = "f-old", Subject = "old", Predicate = "is", Object = "stale",
            Confidence = 0.9, Embedding = OldHigh, CreatedAtUtc = now.AddDays(-200),
        });
        await _facts.UpsertAsync(new Fact
        {
            FactId = "f-fresh", Subject = "fresh", Predicate = "is", Object = "recent",
            Confidence = 0.9, Embedding = FreshLow, CreatedAtUtc = now,
        });

        // Default intent (no override): parity → rank by similarity → old (cosine 1.0) first.
        _ctx.Current = null;
        (await _facts.SearchByVectorAsync(Query, limit: 5))[0].Fact.FactId.Should().Be("f-old");

        // Latest: recency-heavy → fresh promoted.
        _ctx.Current = MemoryRankingOptions.Default.ForIntent(RankingIntent.Latest);
        (await _facts.SearchByVectorAsync(Query, limit: 5))[0].Fact.FactId.Should().Be("f-fresh");

        // Analog over a recency-heavy config: recency is zeroed → the most-similar (old) precedent stays first.
        _ctx.Current = new MemoryRankingOptions { RecencyWeight = 0.9 }.ForIntent(RankingIntent.Analog);
        (await _facts.SearchByVectorAsync(Query, limit: 5))[0].Fact.FactId.Should().Be("f-old");
    }
}
