using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

[Trait("Category", "Integration")]
public sealed class ModuleCliIntegrationTests(Neo4jIntegrationFixture fixture, ITestOutputHelper trace) : IClassFixture<Neo4jIntegrationFixture>
{
    [Fact]
    public async Task ActualCliPlansAppliesRepeatsAndRefusesChangedEmbeddedBackfillWithoutExecutingModuleCode()
    {
        var directory = Directory.CreateTempSubdirectory("agentmemory-module-cli-live-");
        try
        {
            var imagePath = Path.Combine(directory.FullName, "module.dll");
            var original = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ModuleFixtures", "ModuleMigrationFixture.dll"));
            await File.WriteAllBytesAsync(imagePath, original);
            var profilePath = Path.Combine(directory.FullName, "profile.json");
            async Task Profile(byte[] image, bool assembly = true) => await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(new
            {
                format = "agentmemory.modules/v1alpha1",
                stores = new { local = new { provider = "neo4j-direct" } },
                modules = new[] { new Dictionary<string, string>
                {
                    ["id"] = "com.example.migrationfixture", ["version"] = "[0.1,0.2)", ["store"] = "local",
                    [assembly ? "assembly" : "manifest"] = assembly ? "module.dll" : "module.json",
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant()
                } }
            }));
            var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ModuleFixtures", "fixture.agentmemory-module.json"));
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, "module.json"), manifestBytes);
            await Profile(manifestBytes, assembly: false);
            await fixture.TransactionRunner.WriteAsync(async r =>
            {
                await (await r.RunAsync("CREATE (:Migration {version:'cli-base-sentinel', marker:'untouched'})")).ConsumeAsync();
            });
            var before = await Snapshot();
            var planned = await Run("plan", profilePath);
            planned.Exit.Should().Be(0, planned.Error + planned.Output);
            using (var document = JsonDocument.Parse(planned.Output))
            {
                document.RootElement.GetProperty("plan").GetProperty("pending").GetArrayLength().Should().BeGreaterThan(0);
                document.RootElement.GetProperty("hostFeatureEvidence").GetString().Should().Contain("no running host");
            }
            (await Snapshot()).Should().Be(before, "plan must create no schema or ledger rows and change no data");
            var generated = await Run("apply", profilePath, "--actor", "initial-schema");
            generated.Exit.Should().Be(0, generated.Error + generated.Output);
            // Existing data belongs to an already claimed module. Inserting an unowned label before
            // its first migration must be refused, as the retained initial acceptance attempt showed.
            await fixture.TransactionRunner.WriteAsync(async r =>
                await (await r.RunAsync("CREATE (:CliReviewItem {id:'one',owner_id:'fixture',count:0,note:'existing'})")).ConsumeAsync());
            await Profile(original);
            var beforeHandwritten = await Snapshot();
            var handwrittenPlan = await Run("plan", profilePath);
            handwrittenPlan.Exit.Should().Be(0, handwrittenPlan.Error + handwrittenPlan.Output);
            (await Snapshot()).Should().Be(beforeHandwritten);
            var applied = await Run("apply", profilePath, "--actor", "cli-acceptance");
            applied.Exit.Should().Be(0, applied.Error);
            using (var document = JsonDocument.Parse(applied.Output))
            {
                document.RootElement.GetProperty("plan").GetProperty("pending").GetArrayLength().Should().Be(0);
                document.RootElement.GetProperty("actor").GetString().Should().Be("cli-acceptance");
            }
            var after = await Snapshot();
            after.Should().Contain("count=1;");
            after.Should().Contain("base=cli-base-sentinel:untouched;");
            var repeated = await Run("apply", profilePath, "--actor", "another-operator");
            repeated.Exit.Should().Be(0, repeated.Error);
            (await Snapshot()).Should().Be(after, "reapplication must not rerun a backfill or restamp applied actors");

            // Change only the embedded script bytes, then update the outer source pin. The durable
            // migration hash, rather than the profile pin, must refuse this source under the same key.
            var changed = original.ToArray();
            var needle = Encoding.UTF8.GetBytes("n.count + 1");
            var offset = changed.AsSpan().IndexOf(needle);
            offset.Should().BeGreaterThanOrEqualTo(0);
            changed[offset + needle.Length - 1] = (byte)'2';
            await File.WriteAllBytesAsync(imagePath, changed);
            await Profile(changed);
            var refused = await Run("apply", profilePath, "--actor", "changed-source");
            refused.Exit.Should().Be(2, refused.Error);
            using (var document = JsonDocument.Parse(refused.Output))
                document.RootElement.GetProperty("plan").GetProperty("problems").GetArrayLength().Should().BeGreaterThan(0);
            (await Snapshot()).Should().Be(after);
        }
        finally { directory.Delete(recursive: true); }
    }

    private Task<string> Snapshot() => fixture.TransactionRunner.ReadAsync(async r =>
    {
        var row = await (await r.RunAsync("OPTIONAL MATCH (n:CliReviewItem {id:'one'}) RETURN coalesce(n.count,-1) AS count")).SingleAsync();
        var ledger = await (await r.RunAsync("MATCH (m:ModuleMigration) RETURN properties(m) AS row ORDER BY m.module_id,m.key")).ToListAsync();
        var migrations = await (await r.RunAsync("MATCH (m:Migration) RETURN m.version AS version,m.marker AS marker ORDER BY m.version")).ToListAsync();
        var indexes = await (await r.RunAsync("SHOW INDEXES YIELD name RETURN name ORDER BY name")).ToListAsync();
        var constraints = await (await r.RunAsync("SHOW CONSTRAINTS YIELD name RETURN name ORDER BY name")).ToListAsync();
        return "count=" + row["count"] + ";base=" + string.Join(',', migrations.Select(m => m["version"] + ":" + m["marker"]))
            + ";ledger=" + JsonSerializer.Serialize(ledger.Select(m => m["row"]))
            + ";indexes=" + string.Join(',', indexes.Select(i => i["name"]))
            + ";constraints=" + string.Join(',', constraints.Select(i => i["name"]));
    });

    private async Task<(int Exit, string Output, string Error)> Run(string operation, string profile, params string[] extra)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AgentMemory.slnx")))
            root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
        var configuration = typeof(ModuleCliIntegrationTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var cli = Path.Combine(root.FullName, "tools", "AgentMemory.Cli", "bin", configuration, "net10.0", "AgentMemory.Cli.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(profile)! };
        foreach (var arg in new[] { cli, "modules", operation, "--profile", profile, "--uri", fixture.ConnectionString,
            "--user", fixture.User, "--database", "neo4j", "--embedding-dimensions", "4" }.Concat(extra)) start.ArgumentList.Add(arg);
        start.Environment["NEO4J_PASSWORD"] = fixture.Password;
        start.Environment["Neo4j__Password"] = fixture.Password;
        start.Environment["Neo4j__Extensions"] = "";
        start.Environment["NEO4J_EXTENSIONS"] = "";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var stdout = await output;
        var stderr = await error;
        trace.WriteLine(operation + " exit=" + process.ExitCode + Environment.NewLine + stdout + Environment.NewLine + stderr);
        return (process.ExitCode, stdout, stderr);
    }
}
