using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// DRT-5 (decision D, steps 1 to 3): the read time travels with the record; everything waiting is written in one
/// transaction when the decay service can; <see cref="IMemoryAccessTracker.FlushAsync"/> waits for what was tracked.
/// </summary>
public sealed class AccessTrackingQueueDrt5Tests
{
    private static readonly (string, MemoryNodeKind)[] Batch = [("f1", MemoryNodeKind.Fact), ("e1", MemoryNodeKind.Entity)];

    /// <summary>A decay service with the combined write; each write can be held until the test lets it go.</summary>
    private sealed class Recorder : IMemoryDecayService, IMemoryReadRecorder
    {
        public readonly List<IReadOnlyList<MemoryRead>> Writes = [];
        public TaskCompletionSource? Hold;
        public readonly TaskCompletionSource FirstWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StoreAtMost = int.MaxValue;

        public async Task<int> RecordReadsAsync(IReadOnlyList<MemoryRead> reads, CancellationToken cancellationToken = default)
        {
            lock (Writes) Writes.Add(reads);
            FirstWriteStarted.TrySetResult();
            if (Hold is { } hold) await hold.Task;
            return Math.Min(reads.Count, StoreAtMost);
        }

        public Task<int> PruneExpiredMemoriesAsync(MemoryScope? scope = null, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task UpdateAccessTimestampAsync(string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the combined write is used");
        public Task<double> CalculateRetentionScoreAsync(string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default) =>
            Task.FromResult(0d);
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (ServiceProvider Provider, MemoryAccessTrackingChannel Channel) Queue(IMemoryDecayService decay, TimeProvider? clock = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddScoped(_ => decay);
        if (clock is not null) services.AddSingleton<TimeProvider>(clock);
        var provider = services.BuildServiceProvider();
        return (provider, new MemoryAccessTrackingChannel(provider, Options.Create(new MemoryOptions()),
            NullLogger<MemoryAccessTrackingChannel>.Instance));
    }

    [Fact]
    public async Task The_record_carries_the_time_of_the_read_not_of_the_write()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
        var recorder = new Recorder { Hold = new TaskCompletionSource() };
        var (provider, channel) = Queue(recorder, clock);
        await using var _ = provider;

        channel.Track(Batch);
        await recorder.FirstWriteStarted.Task;
        clock.Advance(TimeSpan.FromSeconds(30));
        channel.Track(Batch);
        recorder.Hold.SetResult();
        await channel.FlushAsync();

        recorder.Writes.SelectMany(w => w).Select(r => r.ReadAtUtc).Should().Equal(
            new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 22, 8, 0, 30, TimeSpan.Zero));
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Everything_waiting_is_written_in_one_call()
    {
        var recorder = new Recorder { Hold = new TaskCompletionSource() };
        var (provider, channel) = Queue(recorder);
        await using var _ = provider;

        channel.Track(Batch);
        await recorder.FirstWriteStarted.Task;
        for (var i = 0; i < 10; i++) channel.Track(Batch);
        recorder.Hold.SetResult();
        await channel.FlushAsync();

        recorder.Writes.Select(w => w.Count).Should().Equal(1, 10);
        channel.Counters.Written.Should().Be(11, "written counts recalls, not transactions");
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task One_write_carries_at_most_the_bound()
    {
        var recorder = new Recorder { Hold = new TaskCompletionSource() };
        var (provider, channel) = Queue(recorder);
        await using var _ = provider;

        channel.Track(Batch);
        await recorder.FirstWriteStarted.Task;
        for (var i = 0; i < MemoryAccessTrackingChannel.MaxBatchesPerWrite + 6; i++) channel.Track(Batch);
        recorder.Hold.SetResult();
        await channel.FlushAsync();

        recorder.Writes.Select(w => w.Count).Should().Equal(1, MemoryAccessTrackingChannel.MaxBatchesPerWrite, 6);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task A_decay_service_without_the_combined_write_gets_one_call_per_recall_as_before()
    {
        var decay = Substitute.For<IMemoryDecayService>();
        var (provider, channel) = Queue(decay);
        await using var _ = provider;

        for (var i = 0; i < 5; i++) channel.Track(Batch);
        await channel.FlushAsync();

        await decay.Received(5).UpdateAccessTimestampsAsync(Arg.Any<IReadOnlyCollection<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>());
        channel.Counters.Written.Should().Be(5);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Flush_waits_for_the_write_in_flight()
    {
        var recorder = new Recorder { Hold = new TaskCompletionSource() };
        var (provider, channel) = Queue(recorder);
        await using var _ = provider;

        channel.Track(Batch);
        await recorder.FirstWriteStarted.Task;
        var flush = channel.FlushAsync();
        await Task.Delay(50);
        flush.IsCompleted.Should().BeFalse("the write it waits for is held");

        recorder.Hold.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(10));
        channel.Counters.Written.Should().Be(1);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Flush_with_nothing_tracked_or_after_dispose_returns_at_once()
    {
        var (provider, channel) = Queue(new Recorder());
        await using var _ = provider;

        channel.FlushAsync().IsCompleted.Should().BeTrue();
        channel.Track(Batch);
        await channel.DisposeAsync();
        channel.FlushAsync().IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_combined_write_is_logged_and_the_queue_keeps_going()
    {
        var calls = 0;
        var recorder = Substitute.For<IMemoryDecayService, IMemoryReadRecorder>();
        ((IMemoryReadRecorder)recorder).RecordReadsAsync(Arg.Any<IReadOnlyList<MemoryRead>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? throw new InvalidOperationException("graph down") : Task.FromResult(1));
        var (provider, channel) = Queue(recorder);
        await using var _ = provider;

        channel.Track(Batch);
        await channel.FlushAsync();
        channel.Track(Batch);
        await channel.FlushAsync();

        calls.Should().Be(2);
        channel.Counters.Written.Should().Be(1);
        channel.Counters.Failed.Should().Be(1, "a group the recorder threw on is given up on, and counted");
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Reads_the_recorder_could_not_store_are_counted_as_failed()
    {
        var recorder = new Recorder { Hold = new TaskCompletionSource() };
        var (provider, channel) = Queue(recorder);
        await using var _ = provider;

        channel.Track(Batch);
        await recorder.FirstWriteStarted.Task;
        for (var i = 0; i < 5; i++) channel.Track(Batch);
        recorder.StoreAtMost = 3;
        recorder.Hold.SetResult();
        await channel.FlushAsync();

        channel.Counters.Written.Should().Be(1 + 3);
        channel.Counters.Failed.Should().Be(2);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Flush_returns_when_a_record_was_dropped_by_a_full_queue()
    {
        var recorder = new Recorder { Hold = new TaskCompletionSource() };
        var services = new ServiceCollection().AddLogging();
        services.AddScoped<IMemoryDecayService>(_ => recorder);
        await using var provider = services.BuildServiceProvider();
        var channel = new MemoryAccessTrackingChannel(provider, Options.Create(new MemoryOptions { AccessTrackingQueueCapacity = 1 }),
            NullLogger<MemoryAccessTrackingChannel>.Instance);

        channel.Track(Batch);
        await recorder.FirstWriteStarted.Task;
        for (var i = 0; i < 5; i++) channel.Track(Batch);
        channel.Counters.Dropped.Should().BeGreaterThan(0);
        var flush = channel.FlushAsync();
        recorder.Hold.SetResult();

        await flush.WaitAsync(TimeSpan.FromSeconds(10));
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task A_cancelled_flush_throws_and_the_queue_keeps_draining()
    {
        var recorder = new Recorder { Hold = new TaskCompletionSource() };
        var (provider, channel) = Queue(recorder);
        await using var _ = provider;
        channel.Track(Batch);
        await recorder.FirstWriteStarted.Task;
        using var cancel = new CancellationTokenSource();

        var flush = channel.FlushAsync(cancel.Token);
        cancel.Cancel();

        await flush.Invoking(f => f).Should().ThrowAsync<OperationCanceledException>();
        recorder.Hold.SetResult();
        await channel.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        channel.Counters.Written.Should().Be(1);
        await channel.DisposeAsync();
    }

    [Fact]
    public void The_default_tracker_flush_does_nothing()
    {
        IMemoryAccessTracker tracker = new NothingTracker();
        tracker.FlushAsync().IsCompleted.Should().BeTrue();
    }

    private sealed class NothingTracker : IMemoryAccessTracker
    {
        public void Track(IReadOnlyList<(string NodeId, MemoryNodeKind Kind)> nodes) { }
    }
}
