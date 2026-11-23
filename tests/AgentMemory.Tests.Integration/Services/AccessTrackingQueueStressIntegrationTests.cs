using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// DRT-5b, test T-8 of the read-records design (strategy/performance/database-round-trips/07-read-records-implementation.md):
/// with the queue on by default, saves of turns and recalls of one owner run together for 60 seconds on a real store. No
/// save fails, and every recall's read note is written, dropped and counted, or counted as failed (none on a healthy
/// store): the queue never loses a note silently, and its background writes never break a save.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class AccessTrackingQueueStressIntegrationTests(Neo4jIntegrationFixture fixture) : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await fixture.CleanDatabaseAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(
            configureMemory: _ => { },   // the defaults: the queue is on (DRT-5b)
            configureNeo4j: o =>
            {
                o.Uri = fixture.ConnectionString;
                o.Username = fixture.User;
                o.Password = fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Saves_and_recalls_of_one_owner_together_for_a_minute_lose_no_save_and_no_note()
    {
        using (var seed = _provider.CreateScope())
        {
            var longTerm = seed.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
            for (var i = 0; i < 20; i++)
                await longTerm.AddFactAsync(new Fact
                {
                    FactId = $"stress-fact-{i}", Subject = "user", Predicate = "likes", Object = $"topic {i}", Confidence = 0.9,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
        }

        var until = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(60);
        long saves = 0, savesFailed = 0, recalls = 0, recallsFailed = 0;
        var firstFailure = default(Exception);

        async Task Save(int worker)
        {
            for (var i = 0; until.Elapsed < duration; i++)
            {
                try
                {
                    using var scope = _provider.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IMemoryService>()
                        .AddMessageAsync($"stress-session-{worker}", "stress-conversation", "user", $"turn {i} of worker {worker}: topic {i % 20}");
                    Interlocked.Increment(ref saves);
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref savesFailed);
                    Interlocked.CompareExchange(ref firstFailure, e, null);
                }
            }
        }

        async Task Recall(int worker)
        {
            for (var i = 0; until.Elapsed < duration; i++)
            {
                try
                {
                    using var scope = _provider.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IMemoryService>()
                        .RecallAsync(new RecallRequest { SessionId = $"stress-reader-{worker}", Query = $"what about topic {i % 20}?" });
                    Interlocked.Increment(ref recalls);
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref recallsFailed);
                    Interlocked.CompareExchange(ref firstFailure, e, null);
                }
            }
        }

        await Task.WhenAll([.. Enumerable.Range(0, 2).Select(Save), .. Enumerable.Range(0, 4).Select(Recall)]);

        var tracker = _provider.GetRequiredService<IMemoryAccessTracker>();
        await tracker.FlushAsync();
        var (enqueued, dropped, written, failed) = ((MemoryAccessTrackingChannel)tracker).Counters;

        savesFailed.Should().Be(0, "no save fails while the queue writes: {0}", firstFailure);
        recallsFailed.Should().Be(0, "{0}", firstFailure);
        saves.Should().BePositive();
        recalls.Should().BePositive();
        enqueued.Should().BePositive("the recalls read the seeded facts");
        (written + dropped + failed).Should().Be(enqueued, "every note is written, or dropped and counted, or failed and counted");
        failed.Should().Be(0, "a healthy store fails no group");

        // The notes landed: the seeded facts were read.
        var accessed = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (f:Fact) WHERE f.id STARTS WITH 'stress-fact-' RETURN sum(coalesce(f.access_count, 0)) AS n");
            return global::Neo4j.Driver.ValueExtensions.As<long>((await cursor.SingleAsync())["n"]);
        });
        accessed.Should().BePositive();
    }
}
