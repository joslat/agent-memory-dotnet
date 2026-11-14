using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// Schema bootstrap, the one startup step applications run, applies the migration scripts too. Before 2.3.0 nothing in
/// the library ran them: only the repository's CLI and tests did, so an installed application never got an extension's
/// schema, identity's unique constraints and lookup indexes included.
/// </summary>
public class SchemaBootstrapperMigrationTests
{
    private static INeo4jTransactionRunner Database()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner.WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        txRunner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<CanonicalKeyBackfillRow>>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<CanonicalKeyBackfillRow>()));
        return txRunner;
    }

    private static SchemaBootstrapper Bootstrapper(IMigrationRunner migrations, bool apply = true,
        ILogger<SchemaBootstrapper>? logger = null) =>
        new(Database(), Options.Create(new Neo4jOptions { ApplyMigrationsOnBootstrap = apply }),
            logger ?? NullLogger<SchemaBootstrapper>.Instance, migrations);

    [Fact]
    public async Task Bootstrap_applies_the_migration_scripts()
    {
        var migrations = Substitute.For<IMigrationRunner>();

        await Bootstrapper(migrations).BootstrapAsync();

        await migrations.Received(1).RunMigrationsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Migrations_on_bootstrap_are_the_default()
    {
        new Neo4jOptions().ApplyMigrationsOnBootstrap.Should().BeTrue();
    }

    [Fact]
    public async Task A_host_whose_operator_owns_the_schema_can_keep_bootstrap_from_migrating()
    {
        var migrations = Substitute.For<IMigrationRunner>();
        var logger = new RecordingLogger();

        await Bootstrapper(migrations, apply: false, logger).BootstrapAsync();

        await migrations.DidNotReceive().RunMigrationsAsync(Arg.Any<CancellationToken>());
        logger.Entries.Should().Contain(entry => entry.Message.Contains("ApplyMigrationsOnBootstrap"));
    }

    [Fact]
    public async Task A_migration_the_database_refuses_is_logged_and_bootstrap_completes_as_before()
    {
        // For example a unique constraint over nodes a store without it already duplicated. The store keeps running as it
        // did before migrations ran at startup; the next start tries again.
        var migrations = Substitute.For<IMigrationRunner>();
        migrations.RunMigrationsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(
            new ClientException("Neo.ClientError.Schema.ConstraintCreationFailed", "Unable to create Constraint"));
        var logger = new RecordingLogger();

        await Bootstrapper(migrations, logger: logger).Invoking(b => b.BootstrapAsync()).Should().NotThrowAsync();

        var error = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error).Which;
        error.Message.Should().Contain("Unable to create Constraint").And.Contain("IMigrationRunner");
        logger.Entries.Should().Contain(entry => entry.Message.Contains("Schema bootstrap complete"));
    }

    [Fact]
    public async Task Cancelling_bootstrap_while_it_migrates_still_cancels()
    {
        var migrations = Substitute.For<IMigrationRunner>();
        migrations.RunMigrationsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        await Bootstrapper(migrations).Invoking(b => b.BootstrapAsync()).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_bootstrapper_an_application_resolves_runs_the_migration_runner()
    {
        var migrations = Substitute.For<IMigrationRunner>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Database());
        services.AddSingleton(migrations);
        services.AddNeo4jAgentMemory(_ => { });
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

        await migrations.Received(1).RunMigrationsAsync(Arg.Any<CancellationToken>());
    }

    private sealed class RecordingLogger : ILogger<SchemaBootstrapper>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
