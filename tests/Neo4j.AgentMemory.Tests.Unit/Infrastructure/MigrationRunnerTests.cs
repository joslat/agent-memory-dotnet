using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Infrastructure;

public sealed class MigrationRunnerTests
{
    private static MigrationRunner CreateRunner(INeo4jTransactionRunner txRunner) =>
        new(txRunner, NullLogger<MigrationRunner>.Instance);

    [Fact]
    public async Task RunMigrationsAsync_NoMigrationFolder_DoesNotExecuteAnyTransactions()
    {
        // When the Schema/Migrations folder doesn't exist (typical unit-test environment),
        // the runner should exit early without touching the database.
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var runner = CreateRunner(txRunner);

        await runner.RunMigrationsAsync();

        await txRunner.DidNotReceive()
                      .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunMigrationsAsync_NoMigrationFolder_CompletesWithoutThrowing()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var runner = CreateRunner(txRunner);

        var act = async () => await runner.RunMigrationsAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunMigrationsAsync_CancellationAlreadyRequested_CompletesEarly()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var runner = CreateRunner(txRunner);

        // A pre-cancelled token should not cause the runner to blow up when there are no migration files.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await runner.RunMigrationsAsync(cts.Token);

        // No migration files → exits before checking the token.
        await act.Should().NotThrowAsync();
    }
}
