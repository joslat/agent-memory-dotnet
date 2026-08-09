using AgentMemory.Core.Extraction;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// A host that substitutes its own transaction runner must not be broken by an upgrade.
/// </summary>
/// <remarks>
/// <c>INeo4jTransactionRunner</c> is public and registered with <c>TryAddSingleton</c> — the
/// standard signal that a host may replace it. <c>Neo4jMemoryPersistenceTransaction</c> is then
/// registered with <c>Replace</c>, so it receives whatever the host supplied, and it used to hard-cast
/// that to the later-added <c>INeo4jAtomicTransactionRunner</c> and throw on failure. A substitution
/// that was legal when written became a startup crash on upgrade, with no compile-time warning.
/// <para>
/// Atomicity is optional by design — that is why <c>SupportsAtomicRollback</c> exists and why
/// <c>PersistenceStage</c> branches on it — so the correct behaviour is honest degradation, not a
/// refusal to start and not a false claim of rollback.
/// </para>
/// </remarks>
public sealed class Neo4jPersistenceTransactionFallbackTests
{
    public interface IAtomicRunner : INeo4jTransactionRunner, INeo4jAtomicTransactionRunner;

    [Fact]
    public async Task ANonAtomicRunnerStillConstructsAndRunsTheWork()
    {
        // The load-bearing case: this threw before, taking down startup for a host that had legally
        // replaced a public, TryAdd-registered seam.
        var sut = new Neo4jMemoryPersistenceTransaction(Substitute.For<INeo4jTransactionRunner>());

        var ran = false;
        var result = await sut.ExecuteAsync(_ => { ran = true; return Task.FromResult(42); })
            .ConfigureAwait(true);

        ran.Should().BeTrue();
        result.Should().Be(42);
    }

    [Fact]
    public void ANonAtomicRunnerReportsNoRollbackRatherThanClaimingIt()
    {
        // Degrading silently while still advertising atomicity would be worse than throwing:
        // PersistenceStage would skip its own compensation path believing the store had it covered.
        new Neo4jMemoryPersistenceTransaction(Substitute.For<INeo4jTransactionRunner>())
            .SupportsAtomicRollback.Should().BeFalse();
    }

    [Fact]
    public async Task AnAtomicRunnerIsStillUsedForTheTransaction()
    {
        // The capability must not be lost in the process of making it optional.
        var runner = Substitute.For<IAtomicRunner>();
        runner.ExecuteAtomicWriteAsync(Arg.Any<Func<CancellationToken, Task<int>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(7));

        var sut = new Neo4jMemoryPersistenceTransaction(runner);

        sut.SupportsAtomicRollback.Should().BeTrue();
        (await sut.ExecuteAsync(_ => Task.FromResult(1)).ConfigureAwait(true)).Should().Be(7);
        await runner.Received(1).ExecuteAtomicWriteAsync(
            Arg.Any<Func<CancellationToken, Task<int>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationIsHonouredOnThePassThroughPath()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(true);
        var sut = new Neo4jMemoryPersistenceTransaction(Substitute.For<INeo4jTransactionRunner>());

        var act = () => sut.ExecuteAsync(_ => Task.FromResult(1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }
}
