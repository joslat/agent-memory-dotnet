using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.McpHost.Coding;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingOperationScopeTests
{
    [Fact]
    public async Task Concurrent_operations_keep_their_identity_across_awaits_and_dispose_their_services()
    {
        var owner = new DefaultMemoryOwnerContext { UserId = "outer-owner" };
        var store = new DefaultMemoryStoreContext { ApplicationId = "outer-store" };
        await using var services = Services(owner, store);
        var runner = new CodingOperationScope(services.GetRequiredService<IServiceScopeFactory>());
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var a = new CodingIdentity("owner-a", "alpha");
        var b = new CodingIdentity("owner-b", "beta");
        async Task<ScopedProbe> Work(CodingIdentity expected)
        {
            return await runner.RunAsync(expected, async (provider, ct) =>
            {
                var probe = provider.GetRequiredService<ScopedProbe>();
                if (Interlocked.Increment(ref count) == 2) arrived.SetResult();
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                owner.UserId.Should().Be(expected.OwnerId);
                store.ApplicationId.Should().Be(expected.ProjectId);
                probe.Disposed.Should().BeFalse();
                return probe;
            });
        }
        var probes = await Task.WhenAll(Work(a), Work(b));
        probes[0].Should().NotBeSameAs(probes[1]);
        probes.Should().OnlyContain(p => p.Disposed);
        owner.UserId.Should().Be("outer-owner");
        store.ApplicationId.Should().Be("outer-store");
    }

    [Fact]
    public async Task Failure_restores_identity_and_disposes_the_scope()
    {
        var owner = new DefaultMemoryOwnerContext { UserId = "outer-owner" };
        var store = new DefaultMemoryStoreContext { ApplicationId = "outer-store" };
        await using var services = Services(owner, store);
        var runner = new CodingOperationScope(services.GetRequiredService<IServiceScopeFactory>());
        ScopedProbe? probe = null;
        var failure = async () => await runner.RunAsync<bool>(new("owner", "alpha"), (provider, _) =>
        {
            probe = provider.GetRequiredService<ScopedProbe>();
            owner.UserId.Should().Be("owner");
            store.ApplicationId.Should().Be("alpha");
            throw new InvalidOperationException("expected");
        });
        await failure.Should().ThrowAsync<InvalidOperationException>().WithMessage("expected");
        probe!.Disposed.Should().BeTrue();
        owner.UserId.Should().Be("outer-owner");
        store.ApplicationId.Should().Be("outer-store");
    }

    [Fact]
    public async Task Already_cancelled_work_never_resolves_or_calls_the_operation()
    {
        await using var services = Services(new DefaultMemoryOwnerContext(), new DefaultMemoryStoreContext());
        var runner = new CodingOperationScope(services.GetRequiredService<IServiceScopeFactory>());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var called = false;
        var run = async () => await runner.RunAsync(new("owner", "alpha"), (_, _) =>
        {
            called = true;
            return Task.FromResult(true);
        }, stop.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        called.Should().BeFalse();
    }

    [Fact]
    public async Task Shared_database_configuration_is_rejected_before_any_operation()
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IWritableMemoryOwnerContext>(new DefaultMemoryOwnerContext())
            .AddSingleton<IWritableMemoryStoreContext>(new DefaultMemoryStoreContext())
            .AddSingleton(Options.Create(new MemoryStoreOptions())).BuildServiceProvider();
        var runner = new CodingOperationScope(services.GetRequiredService<IServiceScopeFactory>());
        var called = false;
        var run = async () => await runner.RunAsync(new("owner", "alpha"), (_, _) =>
        {
            called = true;
            return Task.FromResult(true);
        });
        await run.Should().ThrowAsync<InvalidOperationException>();
        called.Should().BeFalse();
    }

    private static ServiceProvider Services(IWritableMemoryOwnerContext owner, IWritableMemoryStoreContext store) =>
        new ServiceCollection().AddSingleton(owner).AddSingleton(store).AddScoped<ScopedProbe>()
            .AddSingleton(Options.Create(new MemoryStoreOptions
            {
                Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = false,
            })).BuildServiceProvider();

    private sealed class ScopedProbe : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
