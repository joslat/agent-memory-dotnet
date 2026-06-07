using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Infrastructure;

public sealed class MigrationRunnerTests
{
    // A directory that is guaranteed not to exist, so the runner exercises its "no migrations"
    // path regardless of which .cypher files ship next to the assembly under test.
    private static readonly string NonExistentDir =
        Path.Combine(AppContext.BaseDirectory, "Schema", "Migrations__unit_test_absent");

    private static MigrationRunner CreateRunner(
        INeo4jTransactionRunner txRunner, string? migrationsDirectory = null) =>
        new(txRunner, NullLogger<MigrationRunner>.Instance, migrationsDirectory ?? NonExistentDir);

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

    // ── ParseStatements (multi-statement .cypher files) ──

    [Fact]
    public void ParseStatements_SplitsOnSemicolon()
    {
        var cypher = "CREATE INDEX a IF NOT EXISTS FOR (n:A) ON (n.x);\n" +
                     "CREATE INDEX b IF NOT EXISTS FOR (n:B) ON (n.y);";

        var statements = MigrationRunner.ParseStatements(cypher);

        statements.Should().HaveCount(2);
        statements[0].Should().Be("CREATE INDEX a IF NOT EXISTS FOR (n:A) ON (n.x)");
        statements[1].Should().Be("CREATE INDEX b IF NOT EXISTS FOR (n:B) ON (n.y)");
    }

    [Fact]
    public void ParseStatements_StripsLineComments()
    {
        var cypher = "// header comment\n" +
                     "CREATE INDEX a IF NOT EXISTS FOR (n:A) ON (n.x); // trailing comment\n" +
                     "// another comment\n" +
                     "CREATE INDEX b IF NOT EXISTS FOR (n:B) ON (n.y);";

        var statements = MigrationRunner.ParseStatements(cypher);

        statements.Should().HaveCount(2);
        statements.Should().NotContain(s => s.Contains("//"));
        statements[0].Should().Be("CREATE INDEX a IF NOT EXISTS FOR (n:A) ON (n.x)");
    }

    [Fact]
    public void ParseStatements_DropsBlankFragments()
    {
        var cypher = "CREATE INDEX a IF NOT EXISTS FOR (n:A) ON (n.x);;\n\n   ;\n";

        var statements = MigrationRunner.ParseStatements(cypher);

        statements.Should().ContainSingle();
        statements[0].Should().Be("CREATE INDEX a IF NOT EXISTS FOR (n:A) ON (n.x)");
    }

    [Fact]
    public void ParseStatements_EmptyOrCommentOnly_ReturnsEmpty()
    {
        MigrationRunner.ParseStatements("").Should().BeEmpty();
        MigrationRunner.ParseStatements("   \n  ").Should().BeEmpty();
        MigrationRunner.ParseStatements("// just a comment\n// and another").Should().BeEmpty();
    }

    // ── Applying a real multi-statement migration ──

    [Fact]
    public async Task RunMigrationsAsync_AppliesEachStatementThenRecordsMigration()
    {
        // Arrange: a temp migration directory with a two-statement .cypher file.
        var dir = Path.Combine(Path.GetTempPath(), "agentmemory-migtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "0099_test.cypher"),
                "// test migration\n" +
                "CREATE INDEX a_idx IF NOT EXISTS FOR (n:A) ON (n.x);\n" +
                "CREATE INDEX b_idx IF NOT EXISTS FOR (n:B) ON (n.y);");

            var executed = new List<string>();
            var txRunner = Substitute.For<INeo4jTransactionRunner>();

            // IsMigrationApplied → false (FetchAsync on an empty cursor returns false by default).
            txRunner
                .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var work = call.Arg<Func<IAsyncQueryRunner, Task<bool>>>();
                    var runner = Substitute.For<IAsyncQueryRunner>();
                    runner.RunAsync(Arg.Any<string>(), Arg.Any<object>())
                          .Returns(_ => Task.FromResult(Substitute.For<IResultCursor>()));
                    return work(runner);
                });

            txRunner
                .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                    var runner = Substitute.For<IAsyncQueryRunner>();
                    runner.RunAsync(Arg.Any<string>())
                          .Returns(ci => { executed.Add(ci.Arg<string>()); return Task.FromResult(Substitute.For<IResultCursor>()); });
                    runner.RunAsync(Arg.Any<string>(), Arg.Any<object>())
                          .Returns(ci => { executed.Add(ci.Arg<string>()); return Task.FromResult(Substitute.For<IResultCursor>()); });
                    return work(runner);
                });

            var runner = CreateRunner(txRunner, dir);

            // Act
            await runner.RunMigrationsAsync();

            // Assert: the migration constraint, both index statements, and the migration record ran.
            executed.Should().Contain(s => s.Contains("CREATE INDEX a_idx"));
            executed.Should().Contain(s => s.Contains("CREATE INDEX b_idx"));
            executed.Should().Contain(s => s.Contains("MERGE (m:Migration"));
            // Index statements never carry a `//` comment fragment.
            executed.Where(s => s.StartsWith("CREATE INDEX")).Should().NotContain(s => s.Contains("//"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
