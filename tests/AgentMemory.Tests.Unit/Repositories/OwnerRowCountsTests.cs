using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// G-14: the owner-first plan's row counts are cached briefly, per store, so a recall does not pay a count query
/// every time, yet an owner that grows is re-counted and one store's count never answers for another's.
/// </summary>
public sealed class OwnerRowCountsTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_count_is_reused_within_the_ttl_and_rerun_after_it()
    {
        var time = new ManualTime();
        var counts = new OwnerRowCounts(time);
        var runs = 0;
        Task<int> Count() => Task.FromResult(++runs * 10);

        (await counts.GetAsync("alice", Count)).Should().Be(10);
        time.Now += OwnerRowCounts.Ttl - TimeSpan.FromSeconds(1);
        (await counts.GetAsync("alice", Count)).Should().Be(10);
        runs.Should().Be(1);

        time.Now += TimeSpan.FromSeconds(1);
        (await counts.GetAsync("alice", Count)).Should().Be(20);
        runs.Should().Be(2);
    }

    [Fact]
    public async Task Each_store_keeps_its_own_count_for_the_same_owner()
    {
        var store = Substitute.For<IMemoryStoreContext>();
        var counts = new OwnerRowCounts(new ManualTime(), store);

        store.ApplicationId.Returns("app-a");
        (await counts.GetAsync("alice", () => Task.FromResult(3))).Should().Be(3);
        store.ApplicationId.Returns("app-b");
        (await counts.GetAsync("alice", () => Task.FromResult(9000))).Should().Be(9000);
        store.ApplicationId.Returns("app-a");
        (await counts.GetAsync("alice", () => Task.FromResult(-1))).Should().Be(3);
    }
}
