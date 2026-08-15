using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.12. Access tracking leaves the recall path without leaving the process.
/// </summary>
/// <remarks>
/// <para>
/// <c>DeferAccessTracking</c> already made this write fire-and-forget, and its own documentation admits
/// the flaw: the write starts inside the request scope, so a host that disposes that scope on response
/// completion disposes the repository under an in-flight write. The option "reads as enabled and does
/// nothing", visible only as an <see cref="ObjectDisposedException"/> in a log nobody reads.
/// </para>
/// <para>
/// The three properties that make the replacement safe rather than merely faster: the queue is owned by
/// the <b>root</b> container and takes a fresh scope per batch; it <b>drops</b> rather than blocks, and
/// counts what it dropped; and it <b>drains on dispose</b>, which is what makes "audit rows equal at end
/// of run" a checkable claim rather than a hope.
/// </para>
/// </remarks>
public sealed class MemoryAccessTrackingChannelTests
{
    private static readonly (string, MemoryNodeKind)[] Batch =
        [("f1", MemoryNodeKind.Fact), ("e1", MemoryNodeKind.Entity)];

    private static ServiceProvider Provider(IMemoryDecayService? decay, int capacity = 1024)
    {
        var services = new ServiceCollection().AddLogging();
        if (decay is not null) services.AddScoped(_ => decay);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new MemoryOptions { AccessTrackingQueueCapacity = capacity }));
        return services.BuildServiceProvider();
    }

    private static MemoryAccessTrackingChannel Channel(
        ServiceProvider provider, int capacity = 1024) =>
        new(provider,
            Microsoft.Extensions.Options.Options.Create(
                new MemoryOptions { AccessTrackingQueueCapacity = capacity }),
            NullLogger<MemoryAccessTrackingChannel>.Instance);

    // ── it writes ─────────────────────────────────────────────────────

    [Fact]
    public async Task AQueuedBatchReachesTheDecayService()
    {
        var decay = Substitute.For<IMemoryDecayService>();
        await using var provider = Provider(decay);
        var channel = Channel(provider);

        channel.Track(Batch);
        // Dispose drains: this is the same guarantee a run's end depends on, so the test exercises it
        // rather than polling for eventual consistency.
        await channel.DisposeAsync();

        await decay.Received(1).UpdateAccessTimestampsAsync(
            Arg.Is<IReadOnlyList<(string, MemoryNodeKind)>>(n => n.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeDrainsEverythingAlreadyQueued()
    {
        // A run that ends promptly must still have recorded what it recalled, or the pre-registered
        // "audit rows equal at end of run" acceptance cannot be checked at all.
        var decay = Substitute.For<IMemoryDecayService>();
        await using var provider = Provider(decay);
        var channel = Channel(provider);

        for (var i = 0; i < 20; i++) channel.Track(Batch);
        await channel.DisposeAsync();

        await decay.Received(20).UpdateAccessTimestampsAsync(
            Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>());
        channel.Counters.Written.Should().Be(20);
    }

    [Fact]
    public async Task AnEmptyBatchIsNotQueuedAtAll()
    {
        var decay = Substitute.For<IMemoryDecayService>();
        await using var provider = Provider(decay);
        var channel = Channel(provider);

        channel.Track([]);
        await channel.DisposeAsync();

        channel.Counters.Enqueued.Should().Be(0);
        await decay.DidNotReceive().UpdateAccessTimestampsAsync(
            Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>());
    }

    // ── it never hurts the caller ─────────────────────────────────────

    [Fact]
    public async Task TrackNeverThrowsEvenWithNoDecayServiceRegistered()
    {
        // The recall path calls this and moves on. An exception here would surface as a failed recall
        // caused by bookkeeping the caller never asked for.
        await using var provider = Provider(decay: null);
        var channel = Channel(provider);

        var act = () => channel.Track(Batch);

        act.Should().NotThrow();
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task AFailingBatchDoesNotKillTheConsumer()
    {
        // A dead consumer turns every subsequent Track into a silent drop for the process's lifetime --
        // strictly worse than the one lost batch that caused it.
        var decay = Substitute.For<IMemoryDecayService>();
        var calls = 0;
        decay.UpdateAccessTimestampsAsync(
                Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? throw new InvalidOperationException("graph down") : Task.CompletedTask);
        await using var provider = Provider(decay);
        var channel = Channel(provider);

        channel.Track(Batch);
        channel.Track(Batch);
        channel.Track(Batch);
        await channel.DisposeAsync();

        calls.Should().Be(3, "the first batch failed and the next two still went through");
        channel.Counters.Written.Should().Be(2);
    }

    // ── it drops rather than blocks ───────────────────────────────────

    [Fact]
    public async Task AFullQueueDropsAndCountsRatherThanBlocking()
    {
        // Waiting would put the latency straight back on the recall path this exists to clear -- and on
        // a stalled database it would do so on every recall at once. A lost access stamp ages one
        // memory's retention marginally against a 30-day half-life; a blocked recall is a failed turn.
        var gate = new TaskCompletionSource();
        var decay = Substitute.For<IMemoryDecayService>();
        decay.UpdateAccessTimestampsAsync(
                Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        await using var provider = Provider(decay, capacity: 1);
        var channel = Channel(provider, capacity: 1);

        // One batch is taken by the consumer and blocks on the gate; the queue then holds one and drops
        // the rest. Track must return immediately every time.
        for (var i = 0; i < 50; i++) channel.Track(Batch);

        channel.Counters.Enqueued.Should().Be(50);
        channel.Counters.Dropped.Should().BeGreaterThan(0, "a capacity-1 queue cannot hold 50 batches");

        gate.SetResult();
        await channel.DisposeAsync();
    }

    [Fact]
    public void TheCapacityDefaultIsTheDocumentedOne()
    {
        new MemoryOptions().AccessTrackingQueueCapacity.Should().Be(1024);
    }

    // ── the option and the registration ───────────────────────────────

    [Fact]
    public void TheQueueIsOffByDefault()
    {
        // Every archived latency measurement was taken on the inline path.
        new MemoryOptions().UseAccessTrackingQueue.Should().BeFalse();
    }

    [Fact]
    public void TheTrackerIsRegisteredAsASingletonOwnedByTheRootContainer()
    {
        // THE placement that makes this safe rather than merely fast. A scoped registration would
        // reproduce exactly the disposal race DeferAccessTracking documents.
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>());
        services.AddAgentMemoryCore(_ => { });

        var descriptor = services.Single(d => d.ServiceType == typeof(IMemoryAccessTracker));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void TheTrackerResolvesFromTheProductionContainer()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>())
            .AddAgentMemoryCore(_ => { })
            .BuildServiceProvider();

        provider.GetService<IMemoryAccessTracker>().Should().NotBeNull();
    }
}
