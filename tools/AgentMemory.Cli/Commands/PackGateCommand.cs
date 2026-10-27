#pragma warning disable AMEXT001 // The gate composes Extensibility preview packs.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Core.Stubs;
using AgentMemory.Extensibility.Ampack;
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
/// <c>evaluate --gate io.agentmemory.packs.codemap --repo . --host-key host.key.pem --activations folder [--variant mediocre|weak] [--output report.json]</c>.
/// <c>--variant mediocre</c> trials the code map with two hops and every fan-out halved; <c>weak</c> with one hop and
/// fan-out 1; each under its own digest. With <c>"baseline": "host"</c>, "without the pack" is what this host already has
/// for code: a grep for the types the changed files declare, its top 3 test files by mentions (the P6 baseline). Both arms
/// are scored per file: a hit when an offered test file is among the changed tests.
/// </summary>
internal sealed partial class PackGateCommand(Action<Neo4jOptions> configureNeo4j, TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<int> ExecuteAsync(string? packId, string? repo, string? hostKeyPath, string? activationsFolder, string? variant, string? outputPath,
        string? packPath = null, string? trustPath = null)
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
        if (variant is not (null or "mediocre" or "weak"))
        {
            output.WriteLine("error: --variant is mediocre, weak or absent.");
            return 2;
        }
        // With --pack, the declaration under trial is the one the verified .ampack carries, not the compiled one.
        var declared = CodeMapPack.Manifest;
        string? packDigest = null;
        LoadedPack? pack = null;
        if (packPath is not null)
        {
            if (trustPath is null || !File.Exists(trustPath))
            {
                output.WriteLine("error: evaluate --gate --pack needs --trust <trust.json>.");
                return 2;
            }
            try
            {
                var loaded = PackLoader.Load(packPath, PackTrust.Load(trustPath));
                if (loaded.Id != packId || loaded.Kind != "definition")
                {
                    output.WriteLine($"error: {packPath} is {loaded.Id} ({loaded.Kind}); the gate trials the {packId} definition.");
                    return 2;
                }
                declared = loaded.Manifest;
                packDigest = loaded.Digest;
                pack = loaded;
            }
            catch (PackLoadException e)
            {
                output.WriteLine("error: " + e.Message);
                return 1;
            }
        }
        var manifest = variant switch { "weak" => Weaken(declared, 1, _ => 1), "mediocre" => Weaken(declared, 2, f => Math.Max(1, f / 2)), _ => declared };
        // The pack itself is composed as a pack, and its decision binds to its digest (PACK-bind); a weakened variant is not that pack.
        var asPack = variant is null ? pack : null;
        ModuleSetBuilder Compose(ModuleSetBuilder m) => asPack is not null ? m.AddPack(asPack) : m.Add(manifest, new CodeMapPack().Configure);
        var trial = manifest.Trial!;
        var tasks = HistoryTasks(repo ?? ".", trial.Tasks);
        output.WriteLine($"{manifest.Id} {manifest.Version}{(variant is null ? "" : " (" + variant + ")")}: {tasks.Count} tasks from this repository's history");
        if (packDigest is not null) output.WriteLine($"  pack     {packPath} ({packDigest}), verified");

        await using var with = await ProviderAsync(m => Compose(m.UseNeo4jStore())).ConfigureAwait(false);
        await using var without = await ProviderAsync(m => m.UseNeo4jStore()).ConfigureAwait(false);
        foreach (var task in tasks) { await CompileAsync(with, task).ConfigureAwait(false); await CompileAsync(without, task).ConfigureAwait(false); }
        var (withArm, details) = await MeasureAsync(with, tasks).ConfigureAwait(false);
        var (emptyArm, _) = await MeasureAsync(without, tasks).ConfigureAwait(false);
        // Without the pack: the host's own retrieval when the trial names it, timed like the host without the pack.
        var withoutArm = trial.Baseline == "host" ? Grep(repo ?? ".", tasks) with { P95Ms = emptyArm.P95Ms } : emptyArm;

        var decision = PackTrialGate.Decide(manifest, withArm, withoutArm, DateTimeOffset.UtcNow, asPack?.Digest);
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
        output.WriteLine($"  without  {(trial.Baseline == "host" ? "grep" : "nothing")}: hit {withoutArm.HitRate:0.000} ({withoutArm.Hits}/{withoutArm.Tasks}), precision {withoutArm.Precision:0.000} ({withoutArm.Correct}/{withoutArm.Offered}), p95 {withoutArm.P95Ms:0.0} ms");
        foreach (var check in decision.Checks)
            output.WriteLine($"  {(check.Met ? "met " : "MISS")}  {check.Target} {check.Observed}{(check.Estimate is { } e ? $" (lower bound; estimate {e})" : "")} (target {(check.Target == "addedP95Ms" ? "≤" : "≥")} {check.Required})");
        output.WriteLine($"  decision {(decision.Active ? "ON" : "OFF")}: {decision.Reason}");
        output.WriteLine($"  signed   {record}");

        // The gate itself: compose again with trials required, from the signed records alone.
        var activations = new PackActivationFolder(activationsFolder, hostKey.ExportSubjectPublicKeyInfoPem());
        await using var gated = await ProviderAsync(m => Compose(m.UseNeo4jStore()).RequireTrials(activations)).ConfigureAwait(false);
        var set = gated.GetRequiredService<ModuleSet>();
        output.WriteLine($"  gate     on: [{string.Join(", ", set.Modules.Select(m => m.Manifest.Id))}]; off: [{string.Join(", ", set.SwitchedOff.Select(o => o.ModuleId + " (" + o.Reason + ")"))}]");

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
            {
                pack = manifest.Id, version = manifest.Version.ToString(), variant, packDigest, manifestDigest = decision.ManifestDigest,
                decision, record = Path.GetFileName(record), gate = new { on = set.Modules.Select(m => m.Manifest.Id), off = set.SwitchedOff }, tasks = details,
            }, Json)).ConfigureAwait(false);
            output.WriteLine($"  report   {outputPath}");
        }
        return 0;
    }

    /// <summary>The code map with fewer hops and narrower fan-outs: a pack that is valid, signed and weaker.</summary>
    private static ModuleManifest Weaken(ModuleManifest manifest, int hops, Func<int, int> fanOut) => manifest with
    {
        Traversal = manifest.Traversal! with { Hops = hops, Relations = [.. manifest.Traversal.Relations.Select(r => r with { FanOut = fanOut(r.FanOut) })] },
    };

    /// <summary>
    /// The host's own retrieval for code, as the P6 analysis defined it: the types the changed files declare, their
    /// whole-word mentions counted in every test file, the top 3 files by count (ties by path).
    /// </summary>
    private static PackTrialMeasurement Grep(string repo, IReadOnlyList<HistoryTask> tasks)
    {
        var root = Path.GetFullPath(repo);
        var tests = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => IsTest(f) && !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)
                && !f.StartsWith(".git/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        string Text(string path) => texts.TryGetValue(path, out var t) ? t : texts[path] = File.ReadAllText(Path.Combine(root, path));
        int hits = 0, offered = 0, correct = 0;
        foreach (var task in tasks)
        {
            var types = task.Anchors.SelectMany(a => Declared().Matches(Text(a)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
            if (types.Count == 0) continue;
            var mention = new Regex(@"\b(" + string.Join("|", types.Order(StringComparer.Ordinal).Select(Regex.Escape)) + @")\b");
            var top = tests.Select(t => (File: t, Count: mention.Matches(Text(t)).Count)).Where(x => x.Count > 0)
                .OrderByDescending(x => x.Count).ThenBy(x => x.File, StringComparer.Ordinal).Take(3).Select(x => x.File).ToList();
            var right = top.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase));
            offered += top.Count;
            correct += right;
            if (right > 0) hits++;
        }
        return new PackTrialMeasurement { Tasks = tasks.Count, Hits = hits, Offered = offered, Correct = correct, P95Ms = 0 };
    }

    [GeneratedRegex(@"\b(?:class|record|struct|interface|enum)\s+(?:struct\s+|class\s+)?([A-Z]\w*)")]
    private static partial Regex Declared();

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
        var perTask = new List<PackTrialTask>();
        foreach (var task in tasks)
        {
            var (envelope, ms) = await CompileAsync(provider, task).ConfigureAwait(false);
            times.Add(ms);
            var files = envelope.Sections.SelectMany(s => s.Items)
                .Where(i => i.Provenance?.RecordModuleId == CodeMapPack.Id && i.Provenance.RecordType == CodeMapPack.TestType)
                .Select(i => TestFile().Match(i.Text)).Where(m => m.Success).Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var right = files.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase));
            offered += files.Count;
            correct += right;
            if (right > 0) hits++;
            perTask.Add(new PackTrialTask(files.Count, right));
            details.Add(new { task.Commit, task.Subject, task.Anchors, task.Tests, offered = files, hit = right > 0 });
        }
        var sorted = times.Order().ToArray();
        var p95 = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
        return (new PackTrialMeasurement { Tasks = tasks.Count, Hits = hits, Offered = offered, Correct = correct, P95Ms = Math.Round(p95, 1), PerTask = perTask },
            details);
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

    /// <summary>A test file: a folder on its path names tests, or the file name ends in Tests.cs or Test.cs; any repository layout.</summary>
    private static bool IsTest(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Split('/')[..^1].Any(s => s.Contains("test", StringComparison.Ordinal))
            || lower.EndsWith("tests.cs", StringComparison.Ordinal) || lower.EndsWith("test.cs", StringComparison.Ordinal);
    }

    /// <summary>Code a change is about: C# outside tests, build output, samples and benchmarks.</summary>
    private static bool IsCode(string path)
    {
        var lower = path.ToLowerInvariant();
        return !IsTest(path) && !lower.Split('/')[..^1].Any(s => s is "obj" or "bin" || s.Contains("sample", StringComparison.Ordinal)
            || s.Contains("benchmark", StringComparison.Ordinal) || s.Contains("example", StringComparison.Ordinal));
    }

    /// <summary>
    /// The most recent commits that changed both code (1 to 8 C# files, the anchors) and tests (the truth), with every file
    /// still present, so the store's code map can answer for them. Any layout: tests are found by <see cref="IsTest"/>.
    /// </summary>
    private static List<HistoryTask> HistoryTasks(string repo, int count)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "-C", repo, "log", "--no-merges", "-n", "2000", "--format=%x1e%H%x1f%s", "--name-only" }) start.ArgumentList.Add(arg);
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var log = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new InvalidOperationException($"git log failed in {repo}.");
        var tasks = new List<HistoryTask>();
        foreach (var entry in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = entry.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var head = lines[0].Split('\x1f');
            var files = lines.Skip(1).Where(f => f.EndsWith(".cs", StringComparison.Ordinal) && File.Exists(Path.Combine(repo, f))).ToList();
            var tests = files.Where(IsTest).ToList();
            var code = files.Where(IsCode).ToList();
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
