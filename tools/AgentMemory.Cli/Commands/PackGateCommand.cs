#pragma warning disable AMEXT001 // The gate composes Extensibility preview packs.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Core.Stubs;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Packs.CodeMap;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Trial-gated activation (pack format v1.1 §4, claim 2): runs a pack's declared trial on this host's own history and records
/// the decision, signed with the host's key. Tasks come from the repository's recent commits: the code files a commit changed
/// are the anchors, the tests it changed are the truth, its subject is the question. The host compiles every task with the
/// pack and without it, checks every target the pack declares, writes the signed decision, and then composes again with
/// trials required, to show which packs are on and why the others are off. Deterministic: no model runs.
/// <c>evaluate --gate io.agentmemory.packs.codemap --repo . --host-key host.key.pem --activations folder [--variant weak] [--output report.json]</c>.
/// <c>--variant weak</c> trials a deliberately weakened code map (one hop, fan-out 1) under its own digest.
/// </summary>
internal sealed partial class PackGateCommand(Action<Neo4jOptions> configureNeo4j, TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<int> ExecuteAsync(string? packId, string? repo, string? hostKeyPath, string? activationsFolder, string? variant, string? outputPath)
    {
        if (packId != CodeMapPack.Id)
        {
            output.WriteLine($"error: evaluate --gate supports {CodeMapPack.Id} (measure test-impact).");
            return 2;
        }
        if (hostKeyPath is null || !File.Exists(hostKeyPath) || activationsFolder is null)
        {
            output.WriteLine("error: evaluate --gate needs --host-key <private.pem> (ampack keygen --name host) and --activations <folder>.");
            return 2;
        }
        if (variant is not (null or "weak"))
        {
            output.WriteLine("error: --variant is weak or absent.");
            return 2;
        }
        var manifest = variant == "weak" ? Weaken(CodeMapPack.Manifest) : CodeMapPack.Manifest;
        var trial = manifest.Trial!;
        var tasks = HistoryTasks(repo ?? ".", trial.Tasks);
        output.WriteLine($"{manifest.Id} {manifest.Version}{(variant is null ? "" : " (" + variant + ")")}: {tasks.Count} tasks from this repository's history");

        await using var with = await ProviderAsync(m => m.UseNeo4jStore().Add(manifest, new CodeMapPack().Configure)).ConfigureAwait(false);
        await using var without = await ProviderAsync(m => m.UseNeo4jStore()).ConfigureAwait(false);
        foreach (var task in tasks) { await CompileAsync(with, task).ConfigureAwait(false); await CompileAsync(without, task).ConfigureAwait(false); }
        var (withArm, details) = await MeasureAsync(with, tasks).ConfigureAwait(false);
        var (withoutArm, _) = await MeasureAsync(without, tasks).ConfigureAwait(false);

        var decision = PackTrialGate.Decide(manifest, withArm, withoutArm, DateTimeOffset.UtcNow);
        using var hostKey = ECDsa.Create();
        hostKey.ImportFromPem(await File.ReadAllTextAsync(hostKeyPath).ConfigureAwait(false));
        Directory.CreateDirectory(activationsFolder);
        var record = Path.Combine(activationsFolder, $"{decision.ModuleId}@{decision.Version}-{decision.DecidedAt:yyyyMMddTHHmmssZ}.dsse.json");
        var statement = PackActivationFolder.Statement(decision);
        var signature = hostKey.SignData(PackActivationFolder.Pae(statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        await File.WriteAllTextAsync(record, JsonSerializer.Serialize(new
        {
            payloadType = "application/vnd.in-toto+json", payload = Convert.ToBase64String(statement),
            signatures = new[] { new { keyid = "host", sig = Convert.ToBase64String(signature) } },
        })).ConfigureAwait(false);

        output.WriteLine($"  with     hit {withArm.HitRate:0.000} ({withArm.Hits}/{withArm.Tasks}), precision {withArm.Precision:0.000} ({withArm.Correct}/{withArm.Offered}), p95 {withArm.P95Ms:0.0} ms");
        output.WriteLine($"  without  hit {withoutArm.HitRate:0.000}, p95 {withoutArm.P95Ms:0.0} ms");
        foreach (var check in decision.Checks)
            output.WriteLine($"  {(check.Met ? "met " : "MISS")}  {check.Target} {check.Observed} (target {(check.Target == "addedP95Ms" ? "≤" : "≥")} {check.Required})");
        output.WriteLine($"  decision {(decision.Active ? "ON" : "OFF")}: {decision.Reason}");
        output.WriteLine($"  signed   {record}");

        // The gate itself: compose again with trials required, from the signed records alone.
        var activations = new PackActivationFolder(activationsFolder, hostKey.ExportSubjectPublicKeyInfoPem());
        await using var gated = await ProviderAsync(m => m.UseNeo4jStore().Add(manifest, new CodeMapPack().Configure).RequireTrials(activations)).ConfigureAwait(false);
        var set = gated.GetRequiredService<ModuleSet>();
        output.WriteLine($"  gate     on: [{string.Join(", ", set.Modules.Select(m => m.Manifest.Id))}]; off: [{string.Join(", ", set.SwitchedOff.Select(o => o.ModuleId + " (" + o.Reason + ")"))}]");

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
            {
                pack = manifest.Id, version = manifest.Version.ToString(), variant, manifestDigest = decision.ManifestDigest,
                decision, record = Path.GetFileName(record), gate = new { on = set.Modules.Select(m => m.Manifest.Id), off = set.SwitchedOff }, tasks = details,
            }, Json)).ConfigureAwait(false);
            output.WriteLine($"  report   {outputPath}");
        }
        return 0;
    }

    /// <summary>The code map with one hop and fan-out 1: a pack that is valid, signed and much weaker.</summary>
    private static ModuleManifest Weaken(ModuleManifest manifest) => manifest with
    {
        Traversal = manifest.Traversal! with { Hops = 1, Relations = [.. manifest.Traversal.Relations.Select(r => r with { FanOut = 1 })] },
    };

    private async Task<ServiceProvider> ProviderAsync(Action<ModuleSetBuilder> modules)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(_ => { }, configureNeo4j);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), 1536));
        services.AddAgentMemoryModules(modules);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var set = scope.ServiceProvider.GetRequiredService<ModuleSet>();
        var applied = await new AgentMemory.Extensibility.Neo4j.Migrations.ModuleMigrator(
            scope.ServiceProvider.GetRequiredService<INeo4jTransactionRunner>(), set.Resolution).ApplyAsync("pack trial").ConfigureAwait(false);
        if (applied.Problems.Count != 0) throw new InvalidOperationException("module migrations failed: " + string.Join("; ", applied.Problems));
        return provider;
    }

    private static async Task<(PackTrialMeasurement Arm, List<object> Details)> MeasureAsync(IServiceProvider provider, IReadOnlyList<HistoryTask> tasks)
    {
        int hits = 0, offered = 0, correct = 0;
        var times = new List<double>();
        var details = new List<object>();
        foreach (var task in tasks)
        {
            var (envelope, ms) = await CompileAsync(provider, task).ConfigureAwait(false);
            times.Add(ms);
            var files = envelope.Sections.SelectMany(s => s.Items)
                .Where(i => i.Provenance?.RecordModuleId == CodeMapPack.Id && i.Provenance.RecordType == CodeMapPack.TestType)
                .Select(i => TestFile().Match(i.Text)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
            var right = files.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase));
            offered += files.Count;
            correct += right;
            if (right > 0) hits++;
            details.Add(new { task.Commit, task.Subject, task.Anchors, task.Tests, offered = files, hit = right > 0 });
        }
        var sorted = times.Order().ToArray();
        var p95 = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
        return (new PackTrialMeasurement { Tasks = tasks.Count, Hits = hits, Offered = offered, Correct = correct, P95Ms = Math.Round(p95, 1) }, details);
    }

    private static async Task<(ContextEnvelope Envelope, double Milliseconds)> CompileAsync(IServiceProvider provider, HistoryTask task)
    {
        await using var scope = provider.CreateAsyncScope();
        var compiler = scope.ServiceProvider.GetRequiredService<IContextCompiler>();
        var watch = Stopwatch.StartNew();
        var envelope = await compiler.CompileAsync(new ContextRequest
        {
            SessionId = "pack-trial", Owner = "pack-trial", Query = task.Subject, Anchors = [.. task.Anchors.Select(a => ContextAnchor.Path(a))],
        }).ConfigureAwait(false);
        return (envelope, watch.Elapsed.TotalMilliseconds);
    }

    private sealed record HistoryTask(string Commit, string Subject, IReadOnlyList<string> Anchors, IReadOnlyList<string> Tests);

    /// <summary>
    /// The most recent commits that changed both code under src/ (1 to 8 C# files, the anchors) and tests (the truth), with
    /// every file still present, so the store's code map can answer for them.
    /// </summary>
    private static List<HistoryTask> HistoryTasks(string repo, int count)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "-C", repo, "log", "--no-merges", "-n", "2000", "--format=%x1e%H%x1f%s", "--name-only" }) start.ArgumentList.Add(arg);
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var log = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new InvalidOperationException($"git log failed in {repo}.");
        static bool IsTest(string f) => f.StartsWith("tests/", StringComparison.Ordinal) || f.Contains(".Tests", StringComparison.Ordinal);
        var tasks = new List<HistoryTask>();
        foreach (var entry in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = entry.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var head = lines[0].Split('\x1f');
            var files = lines.Skip(1).Where(f => f.EndsWith(".cs", StringComparison.Ordinal) && File.Exists(Path.Combine(repo, f))).ToList();
            var tests = files.Where(IsTest).ToList();
            var code = files.Where(f => !IsTest(f) && f.StartsWith("src/", StringComparison.Ordinal)).ToList();
            if (tests.Count == 0 || code.Count is 0 or > 8) continue;
            tasks.Add(new HistoryTask(head[0][..10], head.Length > 1 ? head[1] : "", code, tests));
            if (tasks.Count == count) break;
        }
        return tasks;
    }

    // The code map renders a test as "Test {name} ({file})"; the traversal contributor then adds "(reached from …)".
    [GeneratedRegex(@"\(([^()]+\.cs)\)")]
    private static partial Regex TestFile();
}
