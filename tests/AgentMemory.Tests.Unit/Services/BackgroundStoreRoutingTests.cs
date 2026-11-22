using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Domain.Enrichment;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Enrichment;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Background writes land in the store the work came from (D9).
/// </summary>
/// <remarks>
/// Both background consumers start in their constructor, so they inherited the store-routing context of
/// whichever request first built the singleton: in a multi-store host, access stamps and enrichment for
/// application B were written to application A's store for the life of the process.
/// </remarks>
public sealed class BackgroundStoreRoutingTests
{
    /// <summary>The per-flow store context, as the Neo4j package registers it.</summary>
    private sealed class FlowStoreContext : IWritableMemoryStoreContext
    {
        private readonly AsyncLocal<string?> _applicationId = new();
        public string? ApplicationId { get => _applicationId.Value; set => _applicationId.Value = value; }
    }

    [Fact]
    public async Task Access_stamps_go_to_the_store_they_were_recalled_from()
    {
        var store = new FlowStoreContext();
        var seen = new List<string?>();
        var decay = Substitute.For<IMemoryDecayService>();
        decay.UpdateAccessTimestampsAsync(Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { lock (seen) seen.Add(store.ApplicationId); return Task.CompletedTask; });
        var services = new ServiceCollection();
        services.AddSingleton(decay);
        services.AddSingleton<IMemoryStoreContext>(store);
        await using var provider = services.BuildServiceProvider();

        MemoryAccessTrackingChannel channel;
        using (store.BeginStoreScope("app-a"))       // the request that first built the singleton
            channel = new MemoryAccessTrackingChannel(provider, Options.Create(new MemoryOptions()), NullLogger<MemoryAccessTrackingChannel>.Instance);
        await using var _ = channel;

        using (store.BeginStoreScope("app-b")) channel.Track([("n1", MemoryNodeKind.Fact)]);
        channel.Track([("n2", MemoryNodeKind.Fact)]);   // no store: the default one, not app-a
        for (var i = 0; i < 200 && channel.Counters.Written < 2; i++) await Task.Delay(10);

        lock (seen) seen.Should().Equal("app-b", null);
    }

    /// <summary>DRT-5 (T-7): a combined write whose recalls came from two stores writes each store's part to that store.</summary>
    private sealed class StoreRecorder(FlowStoreContext store) : IMemoryDecayService, IMemoryReadRecorder
    {
        public readonly List<(string? Store, int Reads)> Writes = [];
        public readonly TaskCompletionSource Hold = new();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> RecordReadsAsync(IReadOnlyList<MemoryRead> reads, CancellationToken cancellationToken = default)
        {
            lock (Writes) Writes.Add((store.ApplicationId, reads.Count));
            Started.TrySetResult();
            await Hold.Task;
            return reads.Count;
        }

        public Task<int> PruneExpiredMemoriesAsync(MemoryScope? scope = null, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task UpdateAccessTimestampAsync(string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<double> CalculateRetentionScoreAsync(string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default) => Task.FromResult(0d);
    }

    [Fact]
    public async Task A_combined_write_of_two_stores_writes_each_to_its_own()
    {
        var store = new FlowStoreContext();
        var recorder = new StoreRecorder(store);
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryDecayService>(recorder);
        services.AddSingleton<IMemoryStoreContext>(store);
        await using var provider = services.BuildServiceProvider();
        var channel = new MemoryAccessTrackingChannel(provider, Options.Create(new MemoryOptions()), NullLogger<MemoryAccessTrackingChannel>.Instance);
        await using var _ = channel;

        using (store.BeginStoreScope("app-a")) channel.Track([("n0", MemoryNodeKind.Fact)]);
        await recorder.Started.Task;
        using (store.BeginStoreScope("app-a")) channel.Track([("n1", MemoryNodeKind.Fact)]);
        using (store.BeginStoreScope("app-b")) channel.Track([("n2", MemoryNodeKind.Fact)]);
        using (store.BeginStoreScope("app-a")) channel.Track([("n3", MemoryNodeKind.Fact)]);
        recorder.Hold.SetResult();
        await channel.FlushAsync();

        lock (recorder.Writes) recorder.Writes.Should().Equal(("app-a", 1), ("app-a", 2), ("app-b", 1));
    }

    [Fact]
    public async Task Enrichment_reads_and_writes_the_entitys_own_store()
    {
        var store = new FlowStoreContext();
        var seen = new List<string?>();
        var repo = Substitute.For<IEntityRepository>();
        repo.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            lock (seen) seen.Add(store.ApplicationId);
            return Task.FromResult<Entity?>(null);
        });

        BackgroundEnrichmentQueue queue;
        using (store.BeginStoreScope("app-a"))
            queue = new BackgroundEnrichmentQueue([], repo, Options.Create(new EnrichmentQueueOptions()),
                NullLogger<BackgroundEnrichmentQueue>.Instance, store);
        await using var _ = queue;

        using (store.BeginStoreScope("app-b")) await queue.EnqueueAsync("e1");
        for (var i = 0; i < 200 && seen.Count < 1; i++) await Task.Delay(10);

        lock (seen) seen.Should().Equal("app-b");
    }
}
