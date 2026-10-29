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
        string? packPath = null, string? trustPath = null, string? previousPath = null, string history = "parent")
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
        // The host key is read before the run, not after it (review 2: an hour of trial could end at a key it cannot open),
        // in PEM, or encrypted with the passphrase in AMPACK_KEY_PASSPHRASE as ampack keygen --passphrase-env writes it.
        using var hostKey = ECDsa.Create();
        try
        {
            var pem = await File.ReadAllTextAsync(hostKeyPath).ConfigureAwait(false);
            if (pem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
                hostKey.ImportFromEncryptedPem(pem, Environment.GetEnvironmentVariable("AMPACK_KEY_PASSPHRASE")
                    ?? throw new CryptographicException("the host key is encrypted: set AMPACK_KEY_PASSPHRASE"));
            else hostKey.ImportFromPem(pem);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            output.WriteLine($"error: {hostKeyPath} is not a host signing key: {e.Message}");
            return 2;
        }
        // A weakened variant is an experiment, not a version of the pack: its decisions are kept apart, where no gate reads
        // them, so it can never stand in an upgrade chain as "the version that passed here" (review 2, M10).
        if (variant is not null) activationsFolder = Path.Combine(activationsFolder, "variants");
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
        if (history is not ("parent" or "head"))
        {
            output.WriteLine("error: --history is parent (each task as it stood before its commit: the default) or head (the repository as it is now).");
            return 2;
        }
        // PACK-upgrade-gate: with --previous, the version that passed here before is measured on the same tasks, and an upgrade
        // that declares upgrade.noRegression must not do worse than it.
        LoadedPack? before = null;
        if (previousPath is not null)
        {
            if (trustPath is null || !File.Exists(trustPath))
            {
                output.WriteLine("error: evaluate --gate --previous needs --trust <trust.json>.");
                return 2;
            }
            try { before = PackLoader.Load(previousPath, PackTrust.Load(trustPath)); }
            catch (PackLoadException e)
            {
                output.WriteLine("error: " + e.Message);
                return 1;
            }
            if (before.Id != packId || before.Kind != "definition" || before.Manifest.Version.CompareTo(manifest.Version) >= 0)
            {
                output.WriteLine($"error: {previousPath} is {before.Id} {before.Version} ({before.Kind}); --previous is an earlier version of the {packId} definition.");
                return 2;
            }
        }
        var root = Path.GetFullPath(repo ?? ".");
        var tasks = HistoryTasks(root, trial.Tasks, history);
        output.WriteLine($"{manifest.Id} {manifest.Version}{(variant is null ? "" : " (" + variant + ")")}: {tasks.Count} tasks from this repository's history, "
            + (history == "parent" ? "each at its commit's parent (map and grep as they stood before the change)" : "against the repository as it is now (head)"));
        if (packDigest is not null) output.WriteLine($"  pack     {packPath} ({packDigest}), verified");

        await using var with = await ProviderAsync(m => Compose(m.UseNeo4jStore())).ConfigureAwait(false);
        await using var without = await ProviderAsync(m => m.UseNeo4jStore()).ConfigureAwait(false);
        await using var old = before is null ? null : await ProviderAsync(m => m.UseNeo4jStore().AddPack(before)).ConfigureAwait(false);
        var (withRun, emptyRun, grepRun, oldRun) = (new ArmRun(), new ArmRun(), new ArmRun(), new ArmRun());
        async Task MeasureTaskAsync(string treeRoot, HistoryTask task)
        {
            // A first compile warms the store's caches, so the timing measures the walk, not the first query.
            await CompileAsync(with, task).ConfigureAwait(false);
            await CompileAsync(without, task).ConfigureAwait(false);
            withRun.Add(await MeasureOneAsync(with, task).ConfigureAwait(false));
            emptyRun.Add(await MeasureOneAsync(without, task).ConfigureAwait(false));
            if (trial.Baseline == "host") grepRun.Add(GrepOne(treeRoot, task));
            if (old is not null)
            {
                await CompileAsync(old, task).ConfigureAwait(false);
                oldRun.Add(await MeasureOneAsync(old, task).ConfigureAwait(false));
            }
        }
        if (history == "head")
            foreach (var task in tasks) await MeasureTaskAsync(root, task).ConfigureAwait(false);
        else
        {
            // Review 1, H1: a task is judged as the host would have met it, before its commit. Each task's parent tree is
            // imported into the code map (incrementally: only what changed since the task before, oldest first, closing what
            // is gone), and grep reads that tree too; the truth is the tests the change touched that already existed.
            var runner = with.GetRequiredService<INeo4jTransactionRunner>();
            var importer = new CodeMapCommand(TextWriter.Null);
            output.WriteLine("  note     the store's code map is set to each task's parent in turn, then back to this repository as it is now; "
                + "its history keeps those versions, so run trials against a scratch database where as-of reads matter");
            try
            {
                foreach (var task in tasks.AsEnumerable().Reverse())
                {
                    using var tree = ParentTree.Extract(root, task.Parent);
                    if (await importer.ImportAsync(runner, tree.Path, "git:" + task.Parent).ConfigureAwait(false) != 0)
                        throw new InvalidOperationException($"the code map of {task.Parent[..10]} did not import.");
                    await MeasureTaskAsync(tree.Path, task).ConfigureAwait(false);
                }
            }
            finally
            {
                // Review 2: the host's code map is left as the repository is now, not at the newest task's parent.
                if (await importer.ImportAsync(runner, root, null).ConfigureAwait(false) != 0)
                    output.WriteLine("  warning  the code map could not be re-imported from the repository as it is now: run codemap import --root");
            }
            foreach (var run in new[] { withRun, emptyRun, grepRun, oldRun }) run.Order(tasks);
        }
        var (withArm, details) = withRun.Arm(tasks.Count);
        var (emptyArm, _) = emptyRun.Arm(tasks.Count);
        // Without the pack: the host's own retrieval when the trial names it, timed like the host without the pack.
        var withoutArm = trial.Baseline == "host" ? grepRun.Arm(tasks.Count).Arm with { P95Ms = emptyArm.P95Ms } : emptyArm;
        PackTrialPrevious? previous = null;
        if (before is not null)
        {
            var (beforeArm, _) = oldRun.Arm(tasks.Count);
            previous = new PackTrialPrevious { Version = before.Version, ManifestDigest = PackTrialGate.DigestOf(before.Manifest), PackDigest = before.Digest, With = beforeArm };
            output.WriteLine($"  previous {before.Version} ({before.Digest[..23]}…): hit {beforeArm.HitRate:0.000} ({beforeArm.Hits}/{beforeArm.Tasks}), precision {beforeArm.Precision:0.000} ({beforeArm.Correct}/{beforeArm.Offered})");
        }

        // The record says how the tasks were drawn (review 2): head sees the future, and certify refuses it.
        var decision = PackTrialGate.Decide(manifest, withArm, withoutArm, DateTimeOffset.UtcNow, asPack?.Digest, previous) with { History = history };
        // One writer for every signed record (review 1, M5): named so two decisions never share a file, written whole or not at all.
        var record = PackActivationFolder.Write(activationsFolder, decision, hostKey);

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
                pack = manifest.Id, version = manifest.Version.ToString(), variant, history, packDigest, manifestDigest = decision.ManifestDigest,
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
    private TaskOutcome GrepOne(string root, HistoryTask task)
    {
        // The test files and their texts, read once per tree: the repository as it is (head), or each task's parent tree.
        if (_grepRoot != root)
        {
            _grepRoot = root;
            _grepTests = [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(f => IsTest(f) && !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)
                    && !f.StartsWith(".git/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)];
            _grepTexts.Clear();
        }
        string Text(string path) => _grepTexts.TryGetValue(path, out var t) ? t : _grepTexts[path] = File.ReadAllText(Path.Combine(root, path));
        // An anchor the commit created does not exist before it: it declares nothing grep could search for.
        var types = task.Anchors.Where(a => File.Exists(Path.Combine(root, a)))
            .SelectMany(a => Declared().Matches(Text(a)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        if (types.Count == 0) return new TaskOutcome(task, [], 0, 0, null);
        var mention = new Regex(@"\b(" + string.Join("|", types.Order(StringComparer.Ordinal).Select(Regex.Escape)) + @")\b");
        var top = _grepTests.Select(t => (File: t, Count: mention.Matches(Text(t)).Count)).Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count).ThenBy(x => x.File, StringComparer.Ordinal).Take(3).Select(x => x.File).ToList();
        return new TaskOutcome(task, top, top.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase)), 0, null);
    }

    private string? _grepRoot;
    private List<string> _grepTests = [];
    private readonly Dictionary<string, string> _grepTexts = new(StringComparer.Ordinal);

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

    // One task through one arm: the test files it offered, how many were right, and how long the compile took.
    private static async Task<TaskOutcome> MeasureOneAsync(IServiceProvider provider, HistoryTask task)
    {
        var (envelope, ms) = await CompileAsync(provider, task).ConfigureAwait(false);
        var files = envelope.Sections.SelectMany(s => s.Items)
            .Where(i => i.Provenance?.RecordModuleId == CodeMapPack.Id && i.Provenance.RecordType == CodeMapPack.TestType)
            .Select(i => TestFile().Match(i.Text)).Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new TaskOutcome(task, files, files.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase)), ms, ms);
    }

    private sealed record TaskOutcome(HistoryTask Task, IReadOnlyList<string> Offered, int Right, double Milliseconds, double? Timed);

    // The outcomes of one arm, task by task, and the measurement they make.
    private sealed class ArmRun
    {
        private List<TaskOutcome> _outcomes = [];

        public void Add(TaskOutcome outcome) => _outcomes.Add(outcome);

        // Back into the order the tasks were drawn (most recent first), when they were run oldest first.
        public void Order(IReadOnlyList<HistoryTask> tasks) =>
            _outcomes = [.. _outcomes.OrderBy(o => tasks.ToList().FindIndex(t => ReferenceEquals(t, o.Task)))];

        public (PackTrialMeasurement Arm, List<object> Details) Arm(int tasks)
        {
            var times = _outcomes.Where(o => o.Timed is not null).Select(o => o.Timed!.Value).Order().ToArray();
            var p95 = times.Length == 0 ? 0 : times[Math.Min(times.Length - 1, (int)Math.Ceiling(0.95 * times.Length) - 1)];
            return (new PackTrialMeasurement
            {
                Tasks = tasks, Hits = _outcomes.Count(o => o.Right > 0), Offered = _outcomes.Sum(o => o.Offered.Count), Correct = _outcomes.Sum(o => o.Right),
                P95Ms = Math.Round(p95, 1), PerTask = [.. _outcomes.Select(o => new PackTrialTask(o.Offered.Count, o.Right))],
            }, [.. _outcomes.Select(o => (object)new
            {
                o.Task.Commit, parent = o.Task.Parent[..10], o.Task.Subject, o.Task.Anchors, o.Task.Tests, offered = o.Offered, hit = o.Right > 0,
            })]);
        }
    }

    // A commit's tree, the C# sources and project files a code map reads, in a temporary folder deleted afterwards.
    private sealed class ParentTree : IDisposable
    {
        private ParentTree(string path) => Path = path;

        public string Path { get; }

        public static ParentTree Extract(string repo, string commit)
        {
            var folder = Directory.CreateTempSubdirectory("agentmemory-trial-").FullName;
            try
            {
                var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                // The whole tree, filtered here (review 2: a pathspec that matches nothing makes git archive fail, as for a parent
                // without a Directory.Build.props). Kept: what the code map reads at HEAD, sources, projects and the
                // Directory.Build.props files that turn on implicit usings (without them, calls through List<T>, Task or LINQ do
                // not bind and their links drop out of the parent's map).
                foreach (var arg in new[] { "-C", repo, "archive", "--format=tar", commit }) start.ArgumentList.Add(arg);
                using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
                var errors = git.StandardError.ReadToEndAsync();
                using (var reader = new System.Formats.Tar.TarReader(git.StandardOutput.BaseStream, leaveOpen: true))
                    while (reader.GetNextEntry() is { } entry)
                    {
                        if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile)) continue;
                        var name = entry.Name.Replace('\\', '/');
                        if (!(name.EndsWith(".cs", StringComparison.Ordinal) || name.EndsWith(".csproj", StringComparison.Ordinal)
                              || name.EndsWith("/Directory.Build.props", StringComparison.Ordinal) || name == "Directory.Build.props")) continue;
                        if (name.Split('/').Any(part => part is "" or "." or "..")) continue;
                        var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, name));
                        if (!target.StartsWith(System.IO.Path.GetFullPath(folder) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                        // A name this file system cannot hold (Windows-invalid, or a case twin) is left out, as a checkout leaves it.
                        try
                        {
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                            entry.ExtractToFile(target, overwrite: false);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
                    }
                // The reader stops at the end-of-archive marker; git still writes the record padding after it (up to 10 KiB), and
                // blocks on a full pipe if nobody reads it, so the rest is drained before waiting (it hung on 06f83658 otherwise).
                git.StandardOutput.BaseStream.CopyTo(Stream.Null);
                git.WaitForExit();
                if (git.ExitCode != 0) throw new InvalidOperationException($"git archive {commit[..10]} failed: {errors.Result.Trim()}");
                return new ParentTree(folder);
            }
            catch
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
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

    private sealed record HistoryTask(string Commit, string Parent, string Subject, IReadOnlyList<string> Anchors, IReadOnlyList<string> Tests);

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
    /// The most recent commits that changed both code (1 to 8 C# files, the anchors) and tests (the truth). Any layout: tests
    /// are found by <see cref="IsTest"/>. With <c>parent</c> history (the default) the truth is the tests the change touched
    /// that already existed before it (a test the commit created could not have been offered), and anchors are the code files
    /// it added or modified. With <c>head</c> history (the earlier method) every changed file that still exists counts, which
    /// lets a test the commit created be "found" in a map built afterwards (review 1, H1).
    /// </summary>
    private static List<HistoryTask> HistoryTasks(string repo, int count, string history)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "-C", repo, "log", "--no-merges", "-n", "2000", "--format=%x1e%H%x1f%P%x1f%s", "--name-status" }) start.ArgumentList.Add(arg);
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var log = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new InvalidOperationException($"git log failed in {repo}.");
        var tasks = new List<HistoryTask>();
        foreach (var entry in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = entry.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var head = lines[0].Split('\x1f');
            if (head.Length < 2 || head[1].Length < 40) continue; // a root commit has no parent to stand on
            var changes = lines.Skip(1).Select(l => l.Split('\t')).Where(p => p.Length >= 2 && p[^1].EndsWith(".cs", StringComparison.Ordinal))
                .Select(p => (Status: p[0][0], Path: p[^1])).ToList();
            List<string> tests, code;
            if (history == "parent")
            {
                tests = [.. changes.Where(c => c.Status == 'M' && IsTest(c.Path)).Select(c => c.Path)];
                code = [.. changes.Where(c => c.Status is 'M' or 'A' && IsCode(c.Path)).Select(c => c.Path)];
            }
            else
            {
                var files = changes.Select(c => c.Path).Where(f => File.Exists(Path.Combine(repo, f))).ToList();
                tests = [.. files.Where(IsTest)];
                code = [.. files.Where(IsCode)];
            }
            if (tests.Count == 0 || code.Count is 0 or > 8) continue;
            tasks.Add(new HistoryTask(head[0][..10], head[1].Split(' ')[0], head.Length > 2 ? head[2] : "", code, tests));
            if (tasks.Count == count) break;
        }
        return tasks;
    }

    // The code map renders a test as "Test {name} ({file})"; the traversal contributor then adds "(reached from …)".
    [GeneratedRegex(@"\(([^()]+\.cs)\)")]
    private static partial Regex TestFile();
}
