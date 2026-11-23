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
    public async Task Past_the_ttl_the_count_is_answered_at_once_and_counted_again_in_the_background()
    {
        // DRT-PAUSE: a recall after a pause does not wait for the count; the next one gets the new count.
        var time = new ManualTime();
        var counts = new OwnerRowCounts(time);
        var runs = 0;
        var gate = new TaskCompletionSource();
        async Task<int> Count() { var run = ++runs; if (run > 1) await gate.Task; return run * 10; }

        (await counts.GetAsync("alice", Count, refreshInBackground: true)).Should().Be(10);
        time.Now += OwnerRowCounts.Ttl + TimeSpan.FromSeconds(5);

        var answer = counts.GetAsync("alice", Count, refreshInBackground: true);
        answer.IsCompleted.Should().BeTrue("the cached count is answered without waiting for the new one");
        (await answer).Should().Be(10);
        (await counts.GetAsync("alice", Count, refreshInBackground: true)).Should().Be(10, "one refresh at a time");

        gate.SetResult();
        await counts.Refreshing("alice");
        runs.Should().Be(2);
        (await counts.GetAsync("alice", Count, refreshInBackground: true)).Should().Be(20);
    }

    /// <summary>
    /// PERF-R07-ORDER: the background refresh runs under its own span, memory.background.owner_counts, so its reads are
    /// seen as background work and not charged to the recall that happened to start it (PERF-R-07 counted four reads too
    /// many in the turn that crossed the 30-second mark).
    /// </summary>
    [Fact]
    public async Task The_background_refresh_runs_under_its_own_background_span()
    {
        var time = new ManualTime();
        var counts = new OwnerRowCounts(time);
        string? spanOfTheCount = null;
        Task<int> Count() { spanOfTheCount = System.Diagnostics.Activity.Current?.OperationName; return Task.FromResult(10); }
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemory.Abstractions.Diagnostics.AgentMemoryDiagnostics.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        await counts.GetAsync("alice", Count, refreshInBackground: true);
        spanOfTheCount.Should().NotBe("memory.background.owner_counts", "a first count is waited for, in the recall");
        time.Now += OwnerRowCounts.Ttl + TimeSpan.FromSeconds(5);
        await counts.GetAsync("alice", Count, refreshInBackground: true);
        await counts.Refreshing("alice");

        spanOfTheCount.Should().Be("memory.background.owner_counts");
    }

    [Fact]
    public async Task Past_max_stale_or_inside_a_write_unit_it_is_counted_first_as_before()
    {
        var time = new ManualTime();
        var counts = new OwnerRowCounts(time);
        var runs = 0;
        Task<int> Count() => Task.FromResult(++runs * 10);

        await counts.GetAsync("alice", Count, refreshInBackground: true);
        time.Now += OwnerRowCounts.Ttl + TimeSpan.FromSeconds(1);
        (await counts.GetAsync("alice", Count, refreshInBackground: false)).Should().Be(20, "inside a write unit");

        time.Now += OwnerRowCounts.MaxStale;
        (await counts.GetAsync("alice", Count, refreshInBackground: true)).Should().Be(30, "too old to answer with");
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_cached_count()
    {
        var time = new ManualTime();
        var counts = new OwnerRowCounts(time);
        await counts.GetAsync("alice", () => Task.FromResult(7), refreshInBackground: true);
        time.Now += OwnerRowCounts.Ttl + TimeSpan.FromSeconds(1);

        (await counts.GetAsync("alice", () => Task.FromException<int>(new InvalidOperationException("down")), refreshInBackground: true)).Should().Be(7);
        await counts.Refreshing("alice");

        (await counts.GetAsync("alice", () => Task.FromResult(99), refreshInBackground: true)).Should().Be(7,
            "still within MaxStale: answered, and refreshed again");
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
