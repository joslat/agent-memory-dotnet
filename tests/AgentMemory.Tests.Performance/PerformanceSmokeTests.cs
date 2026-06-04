using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Performance;

/// <summary>
/// Spec §6.6 — performance smoke tests. These are NOT micro-benchmarks: they validate, against a
/// live Neo4j (Testcontainers), that (1) multi-message session persistence works at volume,
/// (2) retrieval latency stays within a generous development threshold, and (3) there is no
/// catastrophic degradation as memory depth grows. Thresholds are deliberately loose so the tests
/// are robust on shared CI runners; actual timings are logged for visibility.
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceSmokeTests : IClassFixture<PerfNeo4jFixture>
{
    // Generous development-grade ceiling for a single recent-message recall. A healthy local/CI
    // round-trip is single-digit ms; multi-hundred-ms only indicates a gross regression.
    private const long RecallCeilingMs = 1500;

    private readonly PerfNeo4jFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly Neo4jMessageRepository _messages;

    public PerformanceSmokeTests(PerfNeo4jFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _messages = new Neo4jMessageRepository(fixture.TransactionRunner, NullLogger<Neo4jMessageRepository>.Instance);
    }

    [Fact]
    public async Task MultiMessageSessionPersistence_PersistsAndRetrievesAll()
    {
        const int count = 30;
        var sessionId = $"perf-persist-{Guid.NewGuid():N}";
        var conversationId = $"{sessionId}-conv";

        var convRepo = new Neo4jConversationRepository(_fixture.TransactionRunner, NullLogger<Neo4jConversationRepository>.Instance);
        await convRepo.UpsertAsync(new Conversation
        {
            ConversationId = conversationId,
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        for (int i = 0; i < count; i++)
        {
            await _messages.AddAsync(new Message
            {
                MessageId = $"{sessionId}-msg-{i}",
                ConversationId = conversationId,
                SessionId = sessionId,
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"message {i}",
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(i)
            });
        }

        var persisted = await CountMessagesAsync(sessionId);
        persisted.Should().Be(count, "every message in a multi-turn session must persist");

        var recent = await _messages.GetRecentBySessionAsync(sessionId, count);
        recent.Should().HaveCount(count);
    }

    [Fact]
    public async Task RetrievalLatency_WithinDevelopmentThreshold()
    {
        var sessionId = $"perf-latency-{Guid.NewGuid():N}";
        await BulkSeedAsync(sessionId, 50);

        var ms = await BestOfAsync(() => _messages.GetRecentBySessionAsync(sessionId, 20), iterations: 5);
        _output.WriteLine($"Recent-recall over 50 messages: {ms} ms (ceiling {RecallCeilingMs} ms)");

        ms.Should().BeLessThan(RecallCeilingMs);
    }

    [Fact]
    public async Task NoCatastrophicDegradation_AsMemoryDepthGrows()
    {
        var shallowSession = $"perf-shallow-{Guid.NewGuid():N}";
        var deepSession = $"perf-deep-{Guid.NewGuid():N}";
        await BulkSeedAsync(shallowSession, 20);
        await BulkSeedAsync(deepSession, 400);

        var shallowMs = await BestOfAsync(() => _messages.GetRecentBySessionAsync(shallowSession, 20), iterations: 5);
        var deepMs = await BestOfAsync(() => _messages.GetRecentBySessionAsync(deepSession, 20), iterations: 5);
        _output.WriteLine($"Recent-recall: depth 20 = {shallowMs} ms, depth 400 = {deepMs} ms (ceiling {RecallCeilingMs} ms)");

        // Absolute ceiling: a bounded recent-recall must stay fast even at 20× the depth.
        deepMs.Should().BeLessThan(RecallCeilingMs);

        // Relative guard: tolerate normal variance but flag super-linear blow-ups. The max() floor
        // avoids false positives when both timings are in the sub-millisecond noise band.
        var allowed = Math.Max(500, shallowMs * 15);
        deepMs.Should().BeLessThan(allowed,
            "bounded recent-recall should not degrade catastrophically as session depth grows");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<long> BestOfAsync(Func<Task> action, int iterations)
    {
        await action(); // warm-up (JIT, connection pool, query plan cache)
        long best = long.MaxValue;
        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        return best;
    }

    /// <summary>Fast bulk insert of well-formed Message nodes (sets every property MapToMessage reads).
    /// Used only as test setup — the measured operation is always the real repository call.</summary>
    private async Task BulkSeedAsync(string sessionId, int count)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            @"UNWIND range(1, $count) AS i
              CREATE (m:Message {
                  id: $sid + '-msg-' + toString(i),
                  conversation_id: $sid + '-conv',
                  session_id: $sid,
                  role: 'user',
                  content: 'message ' + toString(i),
                  timestamp: datetime() - duration({seconds: i})
              })",
            new { sid = sessionId, count });
    }

    private async Task<long> CountMessagesAsync(string sessionId)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (m:Message {session_id: $sid}) RETURN count(m) AS c", new { sid = sessionId });
        var record = await cursor.SingleAsync();
        return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
    }
}
