using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Domain.Enrichment;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Enrichment;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Enrichment;

public sealed class BackgroundEnrichmentQueueTests
{
    // ─── Helpers ────────────────────────────────────────────────────────────

    private static Entity CreateEntity(string id) => new()
    {
        EntityId = id,
        Name = $"Entity-{id}",
        Type = "PLACE",
        Confidence = 1.0,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static EnrichmentResult CreateResult(string name) => new()
    {
        EntityName = name,
        Summary = $"Summary of {name}",
        Provider = "Test"
    };

    private static IEntityRepository CreateRepo(params string[] entityIds)
    {
        var repo = Substitute.For<IEntityRepository>();
        foreach (var id in entityIds)
            repo.GetByIdAsync(id, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Entity?>(CreateEntity(id)));
        repo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<Entity>()));
        return repo;
    }

    private static BackgroundEnrichmentQueue CreateSut(
        IEnrichmentService? service = null,
        IEntityRepository? repo = null,
        EnrichmentQueueOptions? options = null,
        IEnumerable<IEnrichmentService>? services = null)
    {
        var enrichmentServices = services
            ?? (service is not null ? new[] { service } : Array.Empty<IEnrichmentService>());
        return new BackgroundEnrichmentQueue(
            enrichmentServices,
            repo ?? Substitute.For<IEntityRepository>(),
            Options.Create(options ?? new EnrichmentQueueOptions()),
            NullLogger<BackgroundEnrichmentQueue>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        condition().Should().BeTrue(because ?? "condition was never satisfied within the timeout");
    }

    // ─── Tests ──────────────────────────────────────────────────────────────

    // ─── Losing work loudly, not quietly (R2) ───────────────────────────────

    /// <summary>
    /// A full queue drops the oldest item, and says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under <c>DropOldest</c>, <c>TryWrite</c> returns <b>true</b> and discards the oldest queued item.
    /// Any drop counter keyed on that return value reads zero forever while the queue throws work away
    /// — so nothing counted, nothing logged, and an operator whose entities silently stopped being
    /// enriched had nothing to look at.
    /// </para>
    /// <para>
    /// This is the identical defect already found and fixed on <c>MemoryAccessTrackingChannel</c>, where
    /// the test that caught it asserted a non-zero drop count on a capacity-1 queue and got 0. The same
    /// assertion is made here, and it got 0 here too.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFullQueue_CountsAndReportsWhatItDrops()
    {
        // A provider that never returns, so the single worker is occupied and the queue actually fills
        // rather than draining as fast as it is written.
        var blocked = new TaskCompletionSource();
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(async _ => { await blocked.Task; return (EnrichmentResult?)null; });

        var options = new EnrichmentQueueOptions { MaxQueueCapacity = 1, MaxConcurrency = 1 };
        var sut = CreateSut(service, CreateRepo("e1", "e2", "e3", "e4", "e5"), options);

        // try/finally, not straight-line: an assertion failure would otherwise leave the worker parked
        // on `blocked` forever and the queue undisposed, which then fails unrelated tests in this class
        // and buries the real failure under the noise. (Observed exactly that during a red probe.)
        try
        {
            for (var i = 1; i <= 5; i++)
                await sut.EnqueueAsync($"e{i}");

            await WaitUntilAsync(() => sut.Counters.Dropped > 0,
                because: "a capacity-1 queue written five times must record drops, not report zero");

            sut.Counters.Dropped.Should().BeGreaterThan(0);
        }
        finally
        {
            blocked.TrySetResult();
            await sut.DisposeAsync();
        }
    }

    /// <summary>Nothing dropped when the queue never fills — the counter must not cry wolf.</summary>
    [Fact]
    public async Task AQueueThatNeverFills_DropsNothing()
    {
        // A SUCCESS result, deliberately: a null result is classified transient, so the item would be
        // re-queued on a delay and the queue would never settle -- which is the retry path working, not
        // a drop, and it made the first draft of this test hang for its whole timeout.
        var done = new TaskCompletionSource();
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_ => { done.TrySetResult(); return Task.FromResult<EnrichmentResult?>(CreateResult("Entity-e1")); });

        await using var sut = CreateSut(service, CreateRepo("e1"),
            new EnrichmentQueueOptions { MaxQueueCapacity = 64 });

        await sut.EnqueueAsync("e1");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => sut.QueueDepth == 0);

        sut.Counters.Dropped.Should().Be(0);
    }

    /// <summary>
    /// Shutdown with work still queued records what it abandoned.
    /// </summary>
    /// <remarks>
    /// <c>DisposeAsync</c> previously caught <c>TimeoutException</c> with an empty block, so a drain that
    /// ran out of time discarded whatever was queued and reported nothing at all. "Enrichment is slow"
    /// and "enrichment is silently losing work" looked identical from the outside.
    /// </remarks>
    [Fact]
    public async Task ShutdownWithWorkStillQueued_RecordsWhatItAbandons()
    {
        var blocked = new TaskCompletionSource();
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(async _ => { await blocked.Task; return (EnrichmentResult?)null; });

        var sut = CreateSut(service, CreateRepo("e1", "e2", "e3"),
            new EnrichmentQueueOptions { MaxQueueCapacity = 64, MaxConcurrency = 1 });

        try
        {
            await sut.EnqueueAsync("e1");
            await WaitUntilAsync(() => sut.IsProcessing, because: "the worker must be occupied");
            await sut.EnqueueAsync("e2");
            await sut.EnqueueAsync("e3");
            await WaitUntilAsync(() => sut.QueueDepth == 2, because: "two items must be waiting behind it");

            sut.Dispose();

            sut.Counters.AbandonedOnShutdown.Should().Be(2,
                "the two queued items will never be enriched, and that must be recorded rather than lost");
        }
        finally
        {
            // Same reason as above: never leave a worker parked on a TCS that a failed assertion skipped.
            blocked.TrySetResult();
            sut.Dispose();
        }
    }

    /// <summary>Disposing with an empty queue reports nothing abandoned.</summary>
    [Fact]
    public async Task ShutdownWithAnEmptyQueue_AbandonsNothing()
    {
        var sut = CreateSut(repo: CreateRepo("e1"));
        await sut.DisposeAsync();

        sut.Counters.AbandonedOnShutdown.Should().Be(0);
    }

    /// <summary>
    /// Synchronous disposal must not throw, and must not fault the worker task.
    /// </summary>
    /// <remarks>
    /// <c>Dispose()</c> used to call <c>_cts.Dispose()</c> immediately after <c>Cancel()</c>, while the
    /// workers still held that token. A consumer registering a callback on a disposed source throws
    /// <c>ObjectDisposedException</c> inside the worker, faulting the processing task on a path where
    /// nothing observes it. Cancellation alone is what stops the workers.
    /// </remarks>
    [Fact]
    public async Task SynchronousDispose_DoesNotThrow_AndLeavesNoUnobservedFault()
    {
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<EnrichmentResult?>(null));

        var sut = CreateSut(service, CreateRepo("e1", "e2"));
        await sut.EnqueueAsync("e1");

        var dispose = () => sut.Dispose();
        dispose.Should().NotThrow();

        // Give any faulting continuation a chance to surface before the test ends.
        await Task.Delay(100);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Disposing twice is a no-op, by either route.</summary>
    [Fact]
    public async Task DisposingTwice_IsSafe()
    {
        var sut = CreateSut(repo: CreateRepo("e1"));

        sut.Dispose();
        var second = async () => await sut.DisposeAsync();

        await second.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnqueueAsync_SingleEntity_EnrichmentServiceCalled()
    {
        var tcs = new TaskCompletionSource();
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_ => { tcs.TrySetResult(); return Task.FromResult<EnrichmentResult?>(null); });

        var repo = CreateRepo("e1");
        await using var sut = CreateSut(service, repo);

        await sut.EnqueueAsync("e1");

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.Received(1).EnrichEntityAsync("Entity-e1", "PLACE", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_AfterSuccessfulEnrichment_UpsertCalledWithUpdatedEntity()
    {
        var tcs = new TaskCompletionSource();
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(CreateResult("Entity-e1"));

        var repo = CreateRepo("e1");
        repo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { tcs.TrySetResult(); return Task.FromResult(callInfo.Arg<Entity>()); });

        await using var sut = CreateSut(service, repo);
        await sut.EnqueueAsync("e1");

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await repo.Received(1).UpsertAsync(
            Arg.Is<Entity>(e => e.Description == "Summary of Entity-e1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueBatchAsync_AllEntitiesProcessed()
    {
        var processed = new ConcurrentBag<string>();
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   processed.Add(callInfo.ArgAt<string>(0));
                   return Task.FromResult<EnrichmentResult?>(null);
               });

        var repo = CreateRepo("a", "b", "c");
        await using var sut = CreateSut(service, repo);

        await sut.EnqueueBatchAsync(["a", "b", "c"]);

        await WaitUntilAsync(() => processed.Count >= 3);
        processed.Should().BeEquivalentTo(new[] { "Entity-a", "Entity-b", "Entity-c" });
    }

    [Fact]
    public async Task QueueDepth_ReflectsPendingItems_WhileWorkerIsBusy()
    {
        var blockProcessing = new SemaphoreSlim(0);
        var processingStarted = new TaskCompletionSource();

        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(async callInfo =>
               {
                   processingStarted.TrySetResult();
                   await blockProcessing.WaitAsync(callInfo.Arg<CancellationToken>());
                   return (EnrichmentResult?)null;
               });

        var repo = CreateRepo("e1", "e2", "e3");
        var opts = new EnrichmentQueueOptions { MaxConcurrency = 1, RetryDelay = TimeSpan.Zero };
        await using var sut = CreateSut(service, repo, opts);

        // Enqueue 3 items; with MaxConcurrency=1 worker 1 takes e1 and blocks,
        // leaving e2 and e3 in the channel.
        await sut.EnqueueAsync("e1");
        await sut.EnqueueAsync("e2");
        await sut.EnqueueAsync("e3");

        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        sut.QueueDepth.Should().Be(2);

        blockProcessing.Release(10);
    }

    [Fact]
    public async Task IsProcessing_TrueWhileWorkerIsActive()
    {
        var blockProcessing = new SemaphoreSlim(0);
        var processingStarted = new TaskCompletionSource();

        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(async callInfo =>
               {
                   processingStarted.TrySetResult();
                   await blockProcessing.WaitAsync(callInfo.Arg<CancellationToken>());
                   return (EnrichmentResult?)null;
               });

        var repo = CreateRepo("e1");
        await using var sut = CreateSut(service, repo);

        await sut.EnqueueAsync("e1");
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        sut.IsProcessing.Should().BeTrue();

        blockProcessing.Release(1);
    }

    [Fact]
    public async Task IsProcessing_FalseAfterProcessingCompletes()
    {
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<EnrichmentResult?>(CreateResult("Entity-e1")));

        var repo = CreateRepo("e1");
        await using var sut = CreateSut(service, repo, new EnrichmentQueueOptions { MaxRetries = 0 });
        await sut.EnqueueAsync("e1");

        await WaitUntilAsync(() => !sut.IsProcessing, timeoutMs: 5000, "processing should complete");
        sut.IsProcessing.Should().BeFalse();
    }

    [Fact]
    public async Task MaxConcurrency_NeverExceeded()
    {
        const int maxConcurrency = 2;
        var blockProcessing = new SemaphoreSlim(0);
        int currentCount = 0;
        int maxObserved = 0;
        var lockObj = new object();

        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(async callInfo =>
               {
                   lock (lockObj)
                   {
                       currentCount++;
                       if (currentCount > maxObserved) maxObserved = currentCount;
                   }
                   await blockProcessing.WaitAsync(callInfo.Arg<CancellationToken>());
                   lock (lockObj) { currentCount--; }
                   return (EnrichmentResult?)null;
               });

        var repo = CreateRepo("e1", "e2", "e3", "e4", "e5");
        var opts = new EnrichmentQueueOptions { MaxConcurrency = maxConcurrency };
        await using var sut = CreateSut(service, repo, opts);

        await sut.EnqueueBatchAsync(["e1", "e2", "e3", "e4", "e5"]);

        // Wait until both workers are busy
        await WaitUntilAsync(() => currentCount >= maxConcurrency);

        maxObserved.Should().BeLessThanOrEqualTo(maxConcurrency,
            "concurrent processing must never exceed MaxConcurrency");

        blockProcessing.Release(100);
    }

    [Fact]
    public async Task EnqueueAsync_ServiceFailsThenSucceeds_Retries()
    {
        var callCount = 0;
        var result = CreateResult("Entity-e1");
        var upsertTcs = new TaskCompletionSource();

        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   if (Interlocked.Increment(ref callCount) == 1)
                       throw new HttpRequestException("transient error");
                   return Task.FromResult<EnrichmentResult?>(result);
               });

        var repo = CreateRepo("e1");
        repo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { upsertTcs.TrySetResult(); return Task.FromResult(callInfo.Arg<Entity>()); });

        var opts = new EnrichmentQueueOptions { MaxRetries = 2, RetryDelay = TimeSpan.Zero };
        await using var sut = CreateSut(service, repo, opts);

        await sut.EnqueueAsync("e1");

        await upsertTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callCount.Should().BeGreaterThanOrEqualTo(2, "should retry after first failure");
        await repo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_ProviderReturnsTransientRateLimitedThenSuccess_Retries()
    {
        // Providers signal transient failures by RETURNING a non-null Error/RateLimited result (not by
        // throwing). The queue must treat that as a retryable failure, not a success.
        var callCount = 0;
        var upsertTcs = new TaskCompletionSource();
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_ => Interlocked.Increment(ref callCount) == 1
                   ? Task.FromResult<EnrichmentResult?>(new EnrichmentResult { EntityName = "Entity-e1", Provider = "Test", Status = EnrichmentStatus.RateLimited })
                   : Task.FromResult<EnrichmentResult?>(CreateResult("Entity-e1")));

        var repo = CreateRepo("e1");
        repo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => { upsertTcs.TrySetResult(); return Task.FromResult(ci.Arg<Entity>()); });
        var opts = new EnrichmentQueueOptions { MaxRetries = 2, RetryDelay = TimeSpan.Zero };
        await using var sut = CreateSut(service, repo, opts);

        await sut.EnqueueAsync("e1");

        await upsertTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callCount.Should().BeGreaterThanOrEqualTo(2, "a non-null RateLimited result is a transient failure that must be retried");
        await repo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_ProviderReturnsTerminalNotFound_DoesNotRetryOrUpsert()
    {
        // NotFound (and Skipped) are terminal — the entity is genuinely un-enrichable, so the queue must
        // NOT retry (and must not loop) and must not perform a no-op upsert.
        var callCount = 0;
        var doneTcs = new TaskCompletionSource();
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   Interlocked.Increment(ref callCount);
                   doneTcs.TrySetResult();
                   return Task.FromResult<EnrichmentResult?>(new EnrichmentResult { EntityName = "Entity-e1", Provider = "Test", Status = EnrichmentStatus.NotFound });
               });

        var repo = CreateRepo("e1");
        var opts = new EnrichmentQueueOptions { MaxRetries = 2, RetryDelay = TimeSpan.Zero };
        await using var sut = CreateSut(service, repo, opts);

        await sut.EnqueueAsync("e1");

        await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150); // give any erroneous retry a chance to fire
        callCount.Should().Be(1, "NotFound is terminal — it must not retry");
        await repo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_MaxRetriesExceeded_ItemDropped()
    {
        var callCount = 0;
        var droppedTcs = new TaskCompletionSource();

        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var count = Interlocked.Increment(ref callCount);
                   // MaxRetries=1 means 2 total attempts; signal after 2nd attempt
                   if (count >= 2) droppedTcs.TrySetResult();
                   return Task.FromException<EnrichmentResult?>(new HttpRequestException("persistent error"));
               });

        var repo = CreateRepo("e1");
        var opts = new EnrichmentQueueOptions { MaxRetries = 1, RetryDelay = TimeSpan.Zero };
        await using var sut = CreateSut(service, repo, opts);

        await sut.EnqueueAsync("e1");

        await droppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100); // ensure no further retries are queued

        callCount.Should().Be(2, "should attempt exactly MaxRetries+1 times then drop");
        await repo.DidNotReceive().UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueCapacityExceeded_NoExceptionAndQueueDepthCapped()
    {
        var blockProcessing = new SemaphoreSlim(0);
        var firstStarted = new TaskCompletionSource();

        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(async callInfo =>
               {
                   firstStarted.TrySetResult();
                   await blockProcessing.WaitAsync(callInfo.Arg<CancellationToken>());
                   return (EnrichmentResult?)null;
               });

        // capacity = 3, single worker so it takes item 1 and blocks; channel holds up to 3 more
        const int capacity = 3;
        var repo = CreateRepo("e0", "e1", "e2", "e3", "e4", "e5", "e6");
        var opts = new EnrichmentQueueOptions { MaxConcurrency = 1, MaxQueueCapacity = capacity, RetryDelay = TimeSpan.Zero };
        await using var sut = CreateSut(service, repo, opts);

        // Enqueue first item and wait for worker to pick it up
        await sut.EnqueueAsync("e0");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Now fill the channel beyond capacity — should not throw
        var act = async () =>
        {
            for (int i = 1; i <= capacity + 3; i++)
                await sut.EnqueueAsync($"e{i}");
        };
        await act.Should().NotThrowAsync();

        sut.QueueDepth.Should().BeLessThanOrEqualTo(capacity,
            "queue depth must not exceed MaxQueueCapacity");

        blockProcessing.Release(100);
    }

    [Fact]
    public async Task DisposeAsync_StopsProcessingCleanly()
    {
        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<EnrichmentResult?>(null));
        var repo = CreateRepo("e1");

        var sut = CreateSut(service, repo);
        await sut.EnqueueAsync("e1");

        var act = async () => await sut.DisposeAsync();
        await act.Should().NotThrowAsync("DisposeAsync must complete cleanly");
    }

    [Fact]
    public void Dispose_Synchronous_StopsProcessingCleanly()
    {
        var service = Substitute.For<IEnrichmentService>();
        var repo = CreateRepo("e1");

        var sut = CreateSut(service, repo);
        var act = () => sut.Dispose();

        act.Should().NotThrow("Dispose must not throw");
    }

    [Fact]
    public async Task MultipleEnrichmentProviders_AllCalledPerEntity()
    {
        var provider1 = Substitute.For<IEnrichmentService>();
        var provider2 = Substitute.For<IEnrichmentService>();
        var provider3 = Substitute.For<IEnrichmentService>();
        var allCalledTcs = new TaskCompletionSource();
        int callCount = 0;

        provider1.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(_ => { if (Interlocked.Increment(ref callCount) == 3) allCalledTcs.TrySetResult(); return Task.FromResult<EnrichmentResult?>(null); });
        provider2.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(_ => { if (Interlocked.Increment(ref callCount) == 3) allCalledTcs.TrySetResult(); return Task.FromResult<EnrichmentResult?>(null); });
        provider3.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(_ => { if (Interlocked.Increment(ref callCount) == 3) allCalledTcs.TrySetResult(); return Task.FromResult<EnrichmentResult?>(null); });

        var repo = CreateRepo("e1");
        await using var sut = CreateSut(
            repo: repo,
            services: [provider1, provider2, provider3]);

        await sut.EnqueueAsync("e1");

        await allCalledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await provider1.Received(1).EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await provider2.Received(1).EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await provider3.Received(1).EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnabledFalse_ItemsNotProcessed()
    {
        var service = Substitute.For<IEnrichmentService>();
        var repo = CreateRepo("e1");
        var opts = new EnrichmentQueueOptions { Enabled = false };
        await using var sut = CreateSut(service, repo, opts);

        await sut.EnqueueAsync("e1");
        await Task.Delay(100);

        await service.DidNotReceive().EnrichEntityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        sut.QueueDepth.Should().Be(0);
        sut.IsProcessing.Should().BeFalse();
    }

    [Fact]
    public void EnrichmentQueueOptions_DefaultsAreSensible()
    {
        var opts = new EnrichmentQueueOptions();

        opts.MaxConcurrency.Should().Be(3);
        opts.MaxRetries.Should().Be(2);
        opts.RetryDelay.Should().Be(TimeSpan.FromSeconds(5));
        opts.MaxQueueCapacity.Should().Be(1000);
        opts.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task EnqueueAsync_EntityNotFoundInRepo_HandledGracefully()
    {
        var service = Substitute.For<IEnrichmentService>();
        var repo = Substitute.For<IEntityRepository>();
        repo.GetByIdAsync("missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(null));

        var doneTcs = new TaskCompletionSource();
        repo.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                doneTcs.TrySetResult();
                return Task.FromResult<Entity?>(null);
            });

        await using var sut = CreateSut(service, repo);

        var act = async () =>
        {
            await sut.EnqueueAsync("missing");
            await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        await act.Should().NotThrowAsync("missing entity should be logged and skipped, not thrown");
        await service.DidNotReceive().EnrichEntityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueBatchAsync_EmptyCollection_NoError()
    {
        var service = Substitute.For<IEnrichmentService>();
        await using var sut = CreateSut(service);

        var act = async () => await sut.EnqueueBatchAsync([]);

        await act.Should().NotThrowAsync();
        sut.QueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task EnqueueBatchAsync_EnabledFalse_BatchNotProcessed()
    {
        var service = Substitute.For<IEnrichmentService>();
        var repo = CreateRepo("a", "b");
        var opts = new EnrichmentQueueOptions { Enabled = false };
        await using var sut = CreateSut(service, repo, opts);

        await sut.EnqueueBatchAsync(["a", "b"]);
        await Task.Delay(100);

        await service.DidNotReceive().EnrichEntityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_ProviderThrows_OtherProvidersStillCalled()
    {
        var failingProvider = Substitute.For<IEnrichmentService>();
        failingProvider.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                       .Throws(new InvalidOperationException("boom"));

        var successTcs = new TaskCompletionSource();
        var workingProvider = Substitute.For<IEnrichmentService>();
        workingProvider.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                       .Returns(_ =>
                       {
                           successTcs.TrySetResult();
                           return Task.FromResult<EnrichmentResult?>(CreateResult("Entity-e1"));
                       });

        var repo = CreateRepo("e1");
        var upsertTcs = new TaskCompletionSource();
        repo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { upsertTcs.TrySetResult(); return Task.FromResult(callInfo.Arg<Entity>()); });
        await using var sut = CreateSut(repo: repo, services: [failingProvider, workingProvider]);

        await sut.EnqueueAsync("e1");

        await successTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await upsertTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workingProvider.Received(1).EnrichEntityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryThrowsOnOneItem_WorkerSurvivesAndProcessesNextItem()
    {
        // A transient (non-OCE) fault on a repo call must NOT kill the worker. Before the fix the only
        // worker-level catch was for OperationCanceledException, so a GetByIdAsync fault faulted the worker
        // task permanently and every later item went unprocessed (queue silently dies). With MaxConcurrency=1
        // there is exactly one worker, so if it dies on "bad", "good" is never processed and this times out.
        var goodProcessed = new TaskCompletionSource();

        var service = Substitute.For<IEnrichmentService>();
        // "good" succeeds (a real result) so it is not retried — keeps the call count deterministic.
        service.EnrichEntityAsync("Entity-good", Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(_ => { goodProcessed.TrySetResult(); return Task.FromResult<EnrichmentResult?>(CreateResult("Entity-good")); });

        var repo = Substitute.For<IEntityRepository>();
        repo.GetByIdAsync("bad", Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("transient db fault"));
        repo.GetByIdAsync("good", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(CreateEntity("good")));
        repo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Entity>()));

        var opts = new EnrichmentQueueOptions { MaxConcurrency = 1, RetryDelay = TimeSpan.Zero };
        await using var sut = CreateSut(service, repo, opts);

        await sut.EnqueueAsync("bad");
        await sut.EnqueueAsync("good");

        await goodProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.Received(1).EnrichEntityAsync("Entity-good", "PLACE", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_UpdatesEntityDescription_FromEnrichmentSummary()
    {
        var enrichmentResult = new EnrichmentResult
        {
            EntityName = "Entity-e1",
            Summary = "A famous landmark",
            Provider = "Wikipedia"
        };

        var service = Substitute.For<IEnrichmentService>();
        service.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(enrichmentResult);

        Entity? upsertedEntity = null;
        var upsertTcs = new TaskCompletionSource();
        var repo = CreateRepo("e1");
        repo.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                upsertedEntity = callInfo.Arg<Entity>();
                upsertTcs.TrySetResult();
                return Task.FromResult(callInfo.Arg<Entity>());
            });

        await using var sut = CreateSut(service, repo);
        await sut.EnqueueAsync("e1");

        await upsertTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        upsertedEntity.Should().NotBeNull();
        upsertedEntity!.Description.Should().Be("A famous landmark");
    }
}
