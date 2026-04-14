using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Enrichment;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Enrichment;

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
        await using var sut = CreateSut(repo: repo, services: [failingProvider, workingProvider]);

        await sut.EnqueueAsync("e1");

        await successTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workingProvider.Received(1).EnrichEntityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
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
