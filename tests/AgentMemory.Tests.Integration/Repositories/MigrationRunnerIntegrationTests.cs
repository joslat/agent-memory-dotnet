using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Integration tests for <see cref="MigrationRunner"/> against a live Neo4j — discovery, multi-statement
/// parsing, comment stripping, version recording, idempotence, and the DDL effects actually landing.
/// Previously the runner had only mock-level coverage. Uses a controlled temp migration directory (via
/// the internal test-seam ctor) so it never touches the shipped <c>Schema/Migrations</c> scripts, and
/// uses unique versions/labels + cleans up its schema objects so the shared container is unaffected.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class MigrationRunnerIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private string _dir = string.Empty;

    private const string V1 = "9900_itest_first";
    private const string V2 = "9901_itest_second";
    private static readonly string[] Versions = [V1, V2];
    private static readonly string[] IndexNames = ["itest_mig_a_idx", "itest_mig_b_idx", "itest_mig_c_idx"];

    public MigrationRunnerIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "itest-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // First migration: single statement + a `//` comment line (must be stripped).
        File.WriteAllText(Path.Combine(_dir, V1 + ".cypher"),
            "// first migration — comment line must be ignored\n" +
            "CREATE INDEX itest_mig_a_idx IF NOT EXISTS FOR (n:ItestMigNode) ON (n.a);\n");

        // Second migration: two statements in one file (multi-statement parse).
        File.WriteAllText(Path.Combine(_dir, V2 + ".cypher"),
            "// second migration — two statements\n" +
            "CREATE INDEX itest_mig_b_idx IF NOT EXISTS FOR (n:ItestMigNode) ON (n.b);\n" +
            "CREATE INDEX itest_mig_c_idx IF NOT EXISTS FOR (n:ItestMigNode) ON (n.c);\n");

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Drop the test indexes and delete the test Migration nodes so the shared container is pristine.
        // Each schema op runs in its OWN transaction — Neo4j forbids a data write after a schema change
        // within one transaction (the same rule MigrationRunner follows).
        foreach (var idx in IndexNames)
            await _fixture.TransactionRunner.WriteAsync(
                async runner => { await runner.RunAsync($"DROP INDEX {idx} IF EXISTS"); });

        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync("MATCH (m:Migration) WHERE m.version IN $versions DETACH DELETE m",
                new { versions = Versions });
        });

        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private MigrationRunner CreateRunner() =>
        new(_fixture.TransactionRunner, NullLogger<MigrationRunner>.Instance, _dir);

    private Task<long> CountAppliedAsync() =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.version IN $versions RETURN count(m) AS c",
                new { versions = Versions });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

    private Task<long> CountTestIndexesAsync() =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "SHOW INDEXES YIELD name WHERE name IN $names RETURN count(*) AS c",
                new { names = IndexNames });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

    [Fact]
    public async Task RunMigrationsAsync_AppliesAllMigrations_RecordsVersions_AndLandsDdl()
    {
        await CreateRunner().RunMigrationsAsync();

        // Both versions recorded (multi-file discovery in name order).
        (await CountAppliedAsync()).Should().Be(2);
        // All three indexes created — proves multi-statement parsing + comment stripping worked.
        (await CountTestIndexesAsync()).Should().Be(3);

        // The migration-tracking constraint exists.
        var hasConstraint = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "SHOW CONSTRAINTS YIELD name WHERE name = 'migration_version' RETURN count(*) AS c");
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]) > 0;
        });
        hasConstraint.Should().BeTrue();
    }

    [Fact]
    public async Task RunMigrationsAsync_IsIdempotent_SecondRunReAppliesNothing()
    {
        await CreateRunner().RunMigrationsAsync();
        var afterFirst = await CountAppliedAsync();

        // Second run must not throw and must not duplicate Migration nodes (each version applied once).
        await CreateRunner().Invoking(r => r.RunMigrationsAsync()).Should().NotThrowAsync();

        var afterSecond = await CountAppliedAsync();
        afterFirst.Should().Be(2);
        afterSecond.Should().Be(2);
        (await CountTestIndexesAsync()).Should().Be(3);
    }
}
