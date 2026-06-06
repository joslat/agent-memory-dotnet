using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Infrastructure;

public sealed class Neo4jMemoryStoreProvisionerTests
{
    private static Neo4jMemoryStoreProvisioner Create(
        INeo4jSessionFactory factory, MemoryStoreOptions storeOptions) =>
        new(
            factory,
            Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536 }),
            Options.Create(storeOptions),
            NullLogger<Neo4jMemoryStoreProvisioner>.Instance);

    // ── No-op paths (the backward-compatible guarantees) ──

    [Fact]
    public async Task EnsureStoreAsync_SharedDatabase_DoesNotOpenAnySession()
    {
        var factory = Substitute.For<INeo4jSessionFactory>();
        var provisioner = Create(factory, new MemoryStoreOptions { Strategy = MemoryStorageStrategy.SharedDatabase });

        await provisioner.EnsureStoreAsync("acme");

        factory.DidNotReceive().OpenSession(Arg.Any<string>(), Arg.Any<AccessMode>());
        factory.DidNotReceive().OpenSession(Arg.Any<AccessMode>());
    }

    [Fact]
    public async Task EnsureStoreAsync_PerApplication_NullApplicationId_DoesNotOpenAnySession()
    {
        var factory = Substitute.For<INeo4jSessionFactory>();
        var provisioner = Create(factory, new MemoryStoreOptions { Strategy = MemoryStorageStrategy.DatabasePerApplication });

        await provisioner.EnsureStoreAsync(null);
        await provisioner.EnsureStoreAsync("   ");

        factory.DidNotReceive().OpenSession(Arg.Any<string>(), Arg.Any<AccessMode>());
    }

    [Fact]
    public async Task EnsureStoreAsync_AutoProvisionDisabled_DoesNotOpenSession_AndCaches()
    {
        var factory = Substitute.For<INeo4jSessionFactory>();
        var provisioner = Create(factory, new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            AutoProvision = false
        });

        await provisioner.EnsureStoreAsync("acme");
        await provisioner.EnsureStoreAsync("acme");

        factory.DidNotReceive().OpenSession(Arg.Any<string>(), Arg.Any<AccessMode>());
    }

    // ── Provisioning path ──

    [Fact]
    public async Task EnsureStoreAsync_PerApplication_OpensSystemThenStoreDatabase_AndCaches()
    {
        var factory = Substitute.For<INeo4jSessionFactory>();
        factory.OpenSession(Arg.Any<string>(), Arg.Any<AccessMode>())
               .Returns(_ => Substitute.For<IAsyncSession>());

        var provisioner = Create(factory, new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            DatabasePrefix = "mem-"
        });

        await provisioner.EnsureStoreAsync("Acme");
        await provisioner.EnsureStoreAsync("Acme"); // cached → no second round-trip

        // CREATE DATABASE runs against system; schema bootstrap runs against the store database.
        factory.Received(1).OpenSession("system", AccessMode.Write);
        factory.Received(1).OpenSession("mem-acme", AccessMode.Write);
    }

    [Fact]
    public async Task EnsureStoreAsync_PerApplication_BacktickQuotesDatabaseNameInCreateCommand()
    {
        // Regression guard: an unquoted database name with a dash (the default 'mem-' prefix always
        // produces one) is a CREATE DATABASE syntax error. The name must be backtick-quoted. This was
        // caught by a live Enterprise integration test; this locks it without needing Enterprise.
        var captured = new List<string>();
        var systemSession = Substitute.For<IAsyncSession>();
        systemSession
            .ExecuteWriteAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IResultCursor>>>(),
                Arg.Any<Action<TransactionConfigBuilder>>())
            .Returns(async ci =>
            {
                var work = ci.Arg<Func<IAsyncQueryRunner, Task<IResultCursor>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner.RunAsync(Arg.Any<string>()).Returns(c =>
                {
                    captured.Add(c.Arg<string>());
                    return Task.FromResult(Substitute.For<IResultCursor>());
                });
                return await work(runner);
            });

        var factory = Substitute.For<INeo4jSessionFactory>();
        factory.OpenSession("system", Arg.Any<AccessMode>()).Returns(systemSession);
        factory.OpenSession("mem-acme", Arg.Any<AccessMode>()).Returns(Substitute.For<IAsyncSession>());

        var provisioner = Create(factory, new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            DatabasePrefix = "mem-"
        });

        await provisioner.EnsureStoreAsync("Acme");

        captured.Should().ContainSingle(s => s.Contains("CREATE DATABASE"))
            .Which.Should().Contain("CREATE DATABASE `mem-acme`",
                "the database name must be backtick-quoted (dashes are illegal unquoted)");
    }

    [Fact]
    public async Task EnsureStoreAsync_CommunityEdition_ThrowsActionableNotSupported()
    {
        var systemSession = Substitute.For<IAsyncSession>();
        systemSession
            .ExecuteWriteAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IResultCursor>>>(),
                Arg.Any<Action<TransactionConfigBuilder>>())
            .ThrowsAsync(new Neo4jException(
                "Neo.ClientError.Statement.UnsupportedAdministrationCommand",
                "Unsupported administration command: CREATE DATABASE"));

        var factory = Substitute.For<INeo4jSessionFactory>();
        factory.OpenSession("system", Arg.Any<AccessMode>()).Returns(systemSession);

        var provisioner = Create(factory, new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication
        });

        var act = async () => await provisioner.EnsureStoreAsync("acme");

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*Enterprise*");
    }
}
