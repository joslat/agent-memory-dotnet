using System.Diagnostics;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The operator path, end to end: <c>agentmemory migrate --extensions …</c> against a live database.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of this feature checks a link — the flag parses, the option is honoured, the runner
/// reads it. This one runs the actual published entry point an operator types and then asks the
/// database what happened, because the finding being closed was precisely that all the individual links
/// worked and the chain did not exist.
/// </para>
/// <para>
/// It invokes the built CLI as a <b>process</b>, not the command class in-proc. In-proc would skip the
/// top-level statements in <c>Program.cs</c> where the host is configured, which is the only place the
/// gap ever lived.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class CliExtensionMigrationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    /// <summary>Indexes created only by <c>ext/arithmetic/0001</c>.</summary>
    private static readonly string[] ArithmeticIndexes = ["fact_derivation_key_idx", "fact_kind_idx"];

    public CliExtensionMigrationIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Drop only what this test's runs created, and only the ext bookkeeping: base migration history
        // belongs to the shared fixture.
        foreach (var index in ArithmeticIndexes)
        {
            await _fixture.TransactionRunner.WriteAsync(
                async runner => { await runner.RunAsync($"DROP INDEX {index} IF EXISTS"); });
        }

        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.extension_id IS NOT NULL DETACH DELETE m");
        });
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the test must run inside the repository");
        return directory!.FullName;
    }

    private async Task<(int ExitCode, string Output)> RunCliAsync(params string[] arguments)
    {
        var root = RepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(root, "tools", "AgentMemory.Cli"));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        startInfo.ArgumentList.Add("--uri");
        startInfo.ArgumentList.Add(_fixture.ConnectionString);
        startInfo.ArgumentList.Add("--user");
        startInfo.ArgumentList.Add(_fixture.User);
        startInfo.ArgumentList.Add("--password");
        startInfo.ArgumentList.Add(_fixture.Password);
        startInfo.ArgumentList.Add("--embedding-dimensions");
        startInfo.ArgumentList.Add(
            Neo4jIntegrationFixture.TestEmbeddingDimensions.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout + stderr);
    }

    private Task<List<string>> PresentIndexesAsync(string[] names) =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "SHOW INDEXES YIELD name WHERE name IN $names RETURN name ORDER BY name",
                new { names });
            var records = await cursor.ToListAsync();
            return records
                .Select(r => global::Neo4j.Driver.ValueExtensions.As<string>(r["name"]))
                .ToList();
        });

    private Task<List<string>> AppliedExtensionIdsAsync() =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.extension_id IS NOT NULL "
                + "RETURN DISTINCT m.extension_id AS ext ORDER BY ext");
            var records = await cursor.ToListAsync();
            return records
                .Select(r => global::Neo4j.Driver.ValueExtensions.As<string>(r["ext"]))
                .ToList();
        });

    [Fact]
    public async Task MigrateWithAnExtensionAppliesItsDdl()
    {
        // THE test for the finding. Before the fix this command completed successfully and created
        // nothing -- the failure was silent, which is what made it worth a code change rather than a
        // note in the docs.
        var (exitCode, output) = await RunCliAsync("migrate", "--extensions", "arithmetic");

        exitCode.Should().Be(0, "the migrate run failed:\n{0}", output);
        (await PresentIndexesAsync(ArithmeticIndexes)).Should().BeEquivalentTo(ArithmeticIndexes);
        (await AppliedExtensionIdsAsync()).Should().Contain("arithmetic");
    }

    [Fact]
    public async Task MigrateWithoutTheFlagAppliesNoExtensionDdl()
    {
        // The off state. An operator who does not ask for extensions must get exactly the base
        // sequence, or every existing deployment's next migrate run silently gains schema nobody
        // requested.
        var (exitCode, output) = await RunCliAsync("migrate");

        exitCode.Should().Be(0, "the migrate run failed:\n{0}", output);
        (await AppliedExtensionIdsAsync()).Should().BeEmpty();
        (await PresentIndexesAsync(ArithmeticIndexes)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownExtensionFailsBeforeTouchingTheDatabase()
    {
        // Exit 1 with the known ids listed. The registry would refuse this anyway, but from inside the
        // DI graph -- a typo should end in a correction, not a wrapped exception.
        var (exitCode, output) = await RunCliAsync("migrate", "--extensions", "aritmetic");

        exitCode.Should().Be(1);
        output.Should().Contain("aritmetic");
        output.Should().Contain("arithmetic", "the message must name the known ids");
        (await AppliedExtensionIdsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RunningMigrateTwiceWithTheSameExtensionIsANoOp()
    {
        await RunCliAsync("migrate", "--extensions", "arithmetic");
        var first = await AppliedExtensionIdsAsync();

        var (exitCode, output) = await RunCliAsync("migrate", "--extensions", "arithmetic");

        exitCode.Should().Be(0, "the second migrate run failed:\n{0}", output);
        (await AppliedExtensionIdsAsync()).Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task EnablingAnExtensionAfterABaseOnlyMigrateAppliesOnlyTheNewScripts()
    {
        // The realistic adoption path: a database has been migrating for months, then someone turns on
        // an extension. It must apply that extension's scripts and leave everything else alone.
        await RunCliAsync("migrate");
        (await AppliedExtensionIdsAsync()).Should().BeEmpty();

        var (exitCode, output) = await RunCliAsync("migrate", "--extensions", "arithmetic");

        exitCode.Should().Be(0, "the follow-up migrate run failed:\n{0}", output);
        (await AppliedExtensionIdsAsync()).Should().BeEquivalentTo(["arithmetic"]);
        (await PresentIndexesAsync(ArithmeticIndexes)).Should().BeEquivalentTo(ArithmeticIndexes);
    }
}
