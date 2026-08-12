using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// A configured transaction deadline must reach <b>every</b> transaction entry point.
/// </summary>
/// <remarks>
/// <para>
/// Nothing bounded a query before this. The Neo4j driver takes no <see cref="CancellationToken"/> on
/// its transaction APIs, so an in-flight query cannot be interrupted from this side — cooperative
/// cancellation only checks <em>before</em> work starts. A server-enforced timeout is the only thing
/// that actually bounds one.
/// </para>
/// <para>
/// <b>Why each path gets its own test.</b> There are three entry points — managed read, managed write,
/// and the explicit atomic write used by fused persistence — and covering two of three would be worse
/// than covering none: the uncovered one is the longest-running write in the system, and an operator
/// who set a timeout would reasonably believe it applied everywhere.
/// </para>
/// </remarks>
public sealed class TransactionTimeoutTests
{
    private readonly INeo4jSessionFactory _factory = Substitute.For<INeo4jSessionFactory>();
    private readonly IAsyncSession _session = Substitute.For<IAsyncSession>();

    public TransactionTimeoutTests()
    {
        _factory.OpenSession(Arg.Any<AccessMode>()).Returns(_session);
    }

    private Neo4jTransactionRunner CreateSut(TimeSpan? timeout) =>
        new(_factory,
            NullLogger<Neo4jTransactionRunner>.Instance,
            Options.Create(new Neo4jOptions { TransactionTimeout = timeout }));

    [Fact]
    public async Task ReadWithNoTimeoutConfigured_PassesNullConfig_ByteIdenticalToBefore()
    {
        // The byte-identical guarantee for every existing deployment: an unconfigured runner must emit
        // exactly the driver call it emitted before this option existed.
        _session.ExecuteReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(Task.FromResult(7));

        var result = await CreateSut(timeout: null).ReadAsync(_ => Task.FromResult(7));

        result.Should().Be(7);
        await _session.Received(1).ExecuteReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), null);
    }

    [Fact]
    public async Task ReadWithTimeoutConfigured_PassesTransactionConfigToTheDriver()
    {
        _session.ExecuteReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>>())
            .Returns(Task.FromResult(7));

        await CreateSut(TimeSpan.FromSeconds(5)).ReadAsync(_ => Task.FromResult(7));

        await _session.Received(1).ExecuteReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(),
            Arg.Is<Action<TransactionConfigBuilder>?>(c => c != null));
    }

    [Fact]
    public async Task WriteWithTimeoutConfigured_PassesTransactionConfigToTheDriver()
    {
        _session.ExecuteWriteAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>>())
            .Returns(Task.FromResult(7));

        await CreateSut(TimeSpan.FromSeconds(5)).WriteAsync(_ => Task.FromResult(7));

        await _session.Received(1).ExecuteWriteAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(),
            Arg.Is<Action<TransactionConfigBuilder>?>(c => c != null));
    }

    [Fact]
    public async Task WriteWithNoTimeoutConfigured_PassesNullConfig_ByteIdenticalToBefore()
    {
        _session.ExecuteWriteAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(Task.FromResult(7));

        await CreateSut(timeout: null).WriteAsync(_ => Task.FromResult(7));

        await _session.Received(1).ExecuteWriteAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), null);
    }

    [Fact]
    public async Task TheAtomicWritePathIsCoveredToo()
    {
        // The path fused persistence takes. It begins an EXPLICIT transaction rather than a managed one,
        // so it needed wiring of its own -- and it is the longest-running write in the system, i.e. the
        // one an operator most needs bounded.
        var transaction = Substitute.For<IAsyncTransaction>();
        _session.BeginTransactionAsync(Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(Task.FromResult(transaction));

        await CreateSut(TimeSpan.FromSeconds(5))
            .ExecuteAtomicWriteAsync(_ => Task.FromResult(7));

        await _session.Received(1).BeginTransactionAsync(
            Arg.Is<Action<TransactionConfigBuilder>?>(c => c != null));
    }

    [Fact]
    public async Task TheAtomicWritePathIsUnchangedWhenNoTimeoutIsConfigured()
    {
        var transaction = Substitute.For<IAsyncTransaction>();
        _session.BeginTransactionAsync(Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(Task.FromResult(transaction));

        await CreateSut(timeout: null).ExecuteAtomicWriteAsync(_ => Task.FromResult(7));

        await _session.Received(1).BeginTransactionAsync(null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ANonPositiveTimeoutDegradesToNoDeadline_NeverToAZeroOne(int seconds)
    {
        // Defence in depth behind the startup validator: if a non-positive value ever reaches the
        // runner it must degrade to today's behaviour, never be handed to the driver as a deadline --
        // a zero deadline would fail every query instantly.
        //
        // The first version of this test asserted only that the runner constructed. That passes against
        // a runner that hands 0 straight to the driver, i.e. against the exact bug it names.
        _session.ExecuteReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(Task.FromResult(7));

        await CreateSut(TimeSpan.FromSeconds(seconds)).ReadAsync(_ => Task.FromResult(7));

        await _session.Received(1).ExecuteReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), null);
    }

    [Fact]
    public void TheOptionDefaultsToNull()
    {
        new Neo4jOptions().TransactionTimeout.Should().BeNull(
            "no deadline is today's behaviour, and the right value depends on deployment shape");
    }
}
