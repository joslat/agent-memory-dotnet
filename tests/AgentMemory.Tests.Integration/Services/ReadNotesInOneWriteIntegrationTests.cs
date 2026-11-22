using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// DRT-5 (decision D, steps 1 and 2; way A of 07-read-records-implementation.md): the read notes of many recalls are
/// written in one transaction, each with the time of its read, and the stored state is that of one
/// <c>UpdateAccessTimestampsAsync</c> per recall.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ReadNotesInOneWriteIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly IMemoryReadRecorder _recorder;

    public ReadNotesInOneWriteIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        // The write time is a day after every read, so a stamp taken at write time would show.
        var clock = new FixedClock(T0.AddDays(1));
        _recorder = new Neo4jMemoryDecayService(_fixture.TransactionRunner, clock, Options.Create(new MemoryDecayOptions()),
            NullLogger<Neo4jMemoryDecayService>.Instance);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> SeedAsync(string label, int accessCount = 0)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            $"CREATE (n:{label} {{id: $id, owner_id: 'user-A', confidence: 0.9, created_at: datetime($created), access_count: $accessCount}})",
            new { id, created = T0.AddDays(-30).ToString("O"), accessCount });
        return id;
    }

    private async Task<(long Count, DateTimeOffset? Last, List<(DateTimeOffset ReadAt, long Count)> Audits)> ReadAsync(string id)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            @"MATCH (n {id: $id})
              OPTIONAL MATCH (a:MemoryReadAudit {memory_id: $id})
              WITH n, a ORDER BY a.read_at
              RETURN n.access_count AS count, n.last_accessed_at AS last,
                     [x IN collect(a) | [x.read_at, x.access_count]] AS audits", new { id });
        var record = await cursor.SingleAsync();
        DateTimeOffset? last = record["last"] is ZonedDateTime z ? z.ToDateTimeOffset() : null;
        var audits = global::Neo4j.Driver.ValueExtensions.As<List<List<object>>>(record["audits"])
            .Select(pair => (((ZonedDateTime)pair[0]).ToDateTimeOffset(), (long)pair[1])).ToList();
        return (global::Neo4j.Driver.ValueExtensions.As<long>(record["count"]), last, audits);
    }

    [Fact]
    public async Task An_item_read_in_two_recalls_counts_twice_with_each_reads_own_time()
    {
        var fact = await SeedAsync("Fact");
        var entity = await SeedAsync("Entity", accessCount: 4);

        var stored = await _recorder.RecordReadsAsync(
        [
            new MemoryRead([(fact, MemoryNodeKind.Fact), (entity, MemoryNodeKind.Entity), (fact, MemoryNodeKind.Fact)], T0),
            new MemoryRead([(fact, MemoryNodeKind.Fact)], T0.AddSeconds(5)),
        ]);

        stored.Should().Be(2);

        var (count, last, audits) = await ReadAsync(fact);
        count.Should().Be(2, "once per recall: the repeat inside the first recall counts once, as before");
        last.Should().Be(T0.AddSeconds(5), "the time of the last read, not of the write");
        audits.Should().Equal((T0, 1L), (T0.AddSeconds(5), 2L));

        var e = await ReadAsync(entity);
        e.Count.Should().Be(5);
        e.Audits.Should().Equal((T0, 5L));
    }

    [Fact]
    public async Task The_stored_state_equals_one_write_per_recall_with_each_reads_time()
    {
        // R-1 of the read-records design: the same recalls through the per-recall write (each with a clock at its read's
        // time) and through the combined write give the same counts, last read times and audit rows.
        var reads = new[] { T0, T0.AddSeconds(2), T0.AddSeconds(7) };
        var one = new[] { await SeedAsync("Fact"), await SeedAsync("Entity", accessCount: 2), await SeedAsync("Preference") };
        var together = new[] { await SeedAsync("Fact"), await SeedAsync("Entity", accessCount: 2), await SeedAsync("Preference") };
        (string, MemoryNodeKind)[] Recall(string[] ids, int i) => i switch
        {
            0 => [(ids[0], MemoryNodeKind.Fact), (ids[1], MemoryNodeKind.Entity)],
            1 => [(ids[0], MemoryNodeKind.Fact), (ids[2], MemoryNodeKind.Preference), (ids[0], MemoryNodeKind.Fact)],
            _ => [(ids[1], MemoryNodeKind.Entity), (ids[0], MemoryNodeKind.Fact)],
        };

        for (var i = 0; i < reads.Length; i++)
            await new Neo4jMemoryDecayService(_fixture.TransactionRunner, new FixedClock(reads[i]), Options.Create(new MemoryDecayOptions()),
                NullLogger<Neo4jMemoryDecayService>.Instance).UpdateAccessTimestampsAsync(Recall(one, i));
        var stored = await _recorder.RecordReadsAsync(reads.Select((at, i) => new MemoryRead(Recall(together, i), at)).ToList());

        stored.Should().Be(3);
        for (var k = 0; k < 3; k++)
            (await ReadAsync(together[k])).Should().BeEquivalentTo(await ReadAsync(one[k]), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Reads_are_written_in_the_order_handed_over()
    {
        // R-3: the audit rows of an item read by many recalls carry rising counts in the order of the reads.
        var fact = await SeedAsync("Fact");
        var reads = Enumerable.Range(0, 100).Select(i => new MemoryRead([(fact, MemoryNodeKind.Fact)], T0.AddMilliseconds(i))).ToList();

        (await _recorder.RecordReadsAsync(reads)).Should().Be(100);

        var (count, last, audits) = await ReadAsync(fact);
        count.Should().Be(100);
        last.Should().Be(T0.AddMilliseconds(99));
        audits.Select(a => a.Count).Should().Equal(Enumerable.Range(1, 100).Select(i => (long)i));
    }

    [Fact]
    public async Task Nothing_to_write_is_a_no_op()
    {
        var fact = await SeedAsync("Fact", accessCount: 3);

        (await _recorder.RecordReadsAsync([])).Should().Be(0);
        (await _recorder.RecordReadsAsync([new MemoryRead([], T0)])).Should().Be(1, "a recall that returned nothing has nothing to record, and is settled");

        (await ReadAsync(fact)).Count.Should().Be(3);
    }
}
