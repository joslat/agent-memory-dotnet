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
        string? packPath = null, string? trustPath = null, string? previousPath = null, string history = "parent", bool partial = false, string? shadowPath = null, string? tasksPath = null)
    {
        if (tasksPath is null && packId != CodeMapPack.Id)
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
            // A private key: the run ends by signing (review 3: a public key passed this check and failed an hour later).
            _ = hostKey.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            output.WriteLine($"error: {hostKeyPath} is not a host signing key: {e.Message}");
            return 2;
        }
        // A retrieval trial (feature 12; review 11-13, H5: the gate refused every pack but the code map's definition, so it never
        // ran): a knowledge pack, its own trial, the host's task file.
        if (tasksPath is not null)
        {
            if (packPath is null || trustPath is null || !File.Exists(trustPath))
            {
                output.WriteLine("error: a retrieval trial needs --pack <knowledge.ampack> and --trust <trust.json>.");
                return 2;
            }
            LoadedPack knowledgePack;
            try { knowledgePack = PackLoader.Load(packPath, PackTrust.Load(trustPath)); }
            catch (PackLoadException e)
            {
                output.WriteLine("error: " + e.Message);
                return 1;
            }
            if (knowledgePack.Id != packId || knowledgePack.Manifest.Trial is not { Measure: "retrieval" } retrieval)
            {
                output.WriteLine($"error: {packPath} is {knowledgePack.Id}, whose trial is not a retrieval trial; --tasks runs one.");
                return 2;
            }
            return await RetrievalAsync(tasksPath, knowledgePack, knowledgePack.Manifest, retrieval, activationsFolder, hostKey).ConfigureAwait(false);
        }
        // A weakened variant is an experiment, not a version of the pack: its decisions are kept apart, where no gate reads
        // them, so it can never stand in an upgrade chain as "the version that passed here" (review 2, M10).
        if (variant is not null) activationsFolder = Path.Combine(activationsFolder, "variants");
        Func<Task> restore = () => Task.CompletedTask;
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
        if (shadowPath is not null) return DecideFromShadow(shadowPath, root, manifest, trial, asPack?.Digest, activationsFolder, hostKey);
        if (trial.Measure == "retrieval")
        {
            output.WriteLine("error: this pack's trial is a retrieval trial: give the host's task file with --tasks.");
            return 2;
        }
        var tasks = HistoryTasks(root, trial.Tasks, history, out var spacing);
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
            restore = async () =>
            {
                // Review 2, 3: the host's code map is left as the repository is now; a failure says so and masks nothing.
                try
                {
                    if (await importer.ImportAsync(runner, root, null).ConfigureAwait(false) != 0) throw new InvalidOperationException("the import refused");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    output.WriteLine($"  warning  the code map could not be re-imported from the repository as it is now ({e.Message}): run codemap import --root");
                }
            };
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
            catch
            {
                await restore().ConfigureAwait(false);
                throw;
            }
            foreach (var run in new[] { withRun, emptyRun, grepRun, oldRun }) run.Order(tasks);
        }
        // Each arm's cap, fixed before it ran: the pack's maxItems, grep's cut (review 5: every bound assumes one unseen task at it).
        var (withArm, details) = withRun.Arm(tasks.Count);
        // How far apart in history the tasks were drawn: closer than a burst, every bound clusters them (review 6, H4).
        withArm = withArm with { MaximumOffered = manifest.Traversal?.MaxItems, Spacing = spacing };
        var (emptyArm, _) = emptyRun.Arm(tasks.Count);
        emptyArm = emptyArm with { MaximumOffered = 0, Spacing = spacing }; // without the pack nothing offers anything: by design, no uncertainty
        // Without the pack: the host's own retrieval when the trial names it, timed like the host without the pack.
        var withoutArm = trial.Baseline == "host" ? grepRun.Arm(tasks.Count).Arm with { P95Ms = emptyArm.P95Ms, MaximumOffered = GrepTop, Spacing = spacing } : emptyArm;
        PackTrialPrevious? previous = null;
        if (before is not null)
        {
            var (beforeArm, _) = oldRun.Arm(tasks.Count);
            beforeArm = beforeArm with { MaximumOffered = before.Manifest.Traversal?.MaxItems, Spacing = spacing };
            previous = new PackTrialPrevious { Version = before.Version, ManifestDigest = PackTrialGate.DigestOf(before.Manifest), PackDigest = before.Digest, With = beforeArm };
            output.WriteLine($"  previous {before.Version} ({before.Digest[..23]}…): hit {beforeArm.HitRate:0.000} ({beforeArm.Hits}/{beforeArm.Tasks}), precision {beforeArm.Precision:0.000} ({beforeArm.Correct}/{beforeArm.Offered})");
        }

        // The record says how the tasks were drawn (review 2): head sees the future, and certify refuses it.
        PackActivation decision;
        string record;
        try
        {
            // --partial (feature 11, R3): a pack that fails whole may be admitted in the parts that pass alone; an upgrade is judged whole.
            decision = (partial && previous is null
                ? PackTrialGate.DecideParts(manifest, withArm, withoutArm, withRun.Parts(tasks.Count, withArm), DateTimeOffset.UtcNow, asPack?.Digest)
                : PackTrialGate.Decide(manifest, withArm, withoutArm, DateTimeOffset.UtcNow, asPack?.Digest, previous)) with { History = history };
            // One writer for every signed record (review 1, M5): named so two decisions never share a file, written whole or not at all.
            record = PackActivationFolder.Write(activationsFolder, decision, hostKey);
        }
        finally
        {
            // The decision is signed and kept before the store is put back, so a failed restore never loses the run (review 3),
            // and the store is put back whatever happens to the decision (review 4: an unwritable folder left it at a parent).
            await restore().ConfigureAwait(false);
        }

        output.WriteLine($"  with     hit {withArm.HitRate:0.000} ({withArm.Hits}/{withArm.Tasks}), precision {withArm.Precision:0.000} ({withArm.Correct}/{withArm.Offered}), p95 {withArm.P95Ms:0.0} ms{Bytes(withArm)}");
        output.WriteLine($"  without  {(trial.Baseline == "host" ? "grep" : "nothing")}: hit {withoutArm.HitRate:0.000} ({withoutArm.Hits}/{withoutArm.Tasks}), precision {withoutArm.Precision:0.000} ({withoutArm.Correct}/{withoutArm.Offered}), p95 {withoutArm.P95Ms:0.0} ms{Bytes(withoutArm)}");
        foreach (var check in decision.Checks)
            output.WriteLine($"  {(check.Met ? "met " : "MISS")}  {check.Target} {check.Observed}{(check.Estimate is { } e ? $" (lower bound; estimate {e})" : "")} (target {(PackRegistry.AtMost.Contains(check.Target) ? "≤" : "≥")} {check.Required})");
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

    // What an arm put into the context (feature 05): the median bytes a task, framed as the model reads them, and the context
    // efficiency, correct items per 1,000 bytes (a display value, never a target: a ratio of sums).
    private static string Bytes(PackTrialMeasurement arm)
    {
        if (arm.PerTask is not { Count: > 0 } tasks || tasks.Any(t => t.OfferedBytes is null)) return "";
        var sorted = tasks.Select(t => t.OfferedBytes!.Value).Order().ToArray();
        var total = sorted.Sum();
        return $", median {sorted[(sorted.Length - 1) / 2]} bytes a task, {(total == 0 ? 0 : arm.Correct * 1000.0 / total):0.00} correct per 1,000 bytes";
    }

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
        if (types.Count == 0) return new TaskOutcome(task, [], 0, 0, null, 0);
        var mention = new Regex(@"\b(" + string.Join("|", types.Order(StringComparer.Ordinal).Select(Regex.Escape)) + @")\b");
        var top = _grepTests.Select(t => (File: t, Count: mention.Matches(Text(t)).Count)).Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count).ThenBy(x => x.File, StringComparer.Ordinal).Take(GrepTop).Select(x => x.File).ToList();
        // What the host's own retrieval shows the model of each file (feature 05): its path and the first line that mentions a
        // declared type, framed as the provider frames a pack's records.
        var bytes = top.Sum(f => PackTrialRender.Bytes("grep", f, Text(f).Split('\n').FirstOrDefault(l => mention.IsMatch(l))?.Trim() ?? ""));
        return new TaskOutcome(task, top, top.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase)), 0, null, bytes);
    }

    // How many test files the grep baseline offers a task: its cut, and its arm's cap for the bounds (review 5, root RB).
    private const int GrepTop = 3;

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

    /// <summary>
    /// <c>agentmemory trial [--repo .]</c> (feature 14): the code map's trial on this repository, with no database and no network.
    /// Each task's parent tree is indexed into a snapshot and walked in memory, as a host holding the pack in memory walks it; grep
    /// is the baseline; the decision is the same exact decision, signed with this machine's host key (made on first run, under
    /// <c>~/.agentmemory</c>). One screen: each arm's hits, precision and context, the checks, and the decision.
    /// </summary>
    public async Task<int> TrialInMemoryAsync(string? repo, string? hostKeyPath, string? activationsFolder)
    {
        var root = Path.GetFullPath(repo ?? ".");
        var manifest = CodeMapPack.Manifest;
        var trial = manifest.Trial!;
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmemory");
        hostKeyPath ??= Path.Combine(home, "host.key.pem");
        activationsFolder ??= Path.Combine(home, "activations");
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(hostKeyPath)) hostKey.ImportFromPem(File.ReadAllText(hostKeyPath));
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(hostKeyPath)!);
            File.WriteAllText(hostKeyPath, hostKey.ExportPkcs8PrivateKeyPem());
            output.WriteLine($"  made a host key for this machine: {hostKeyPath}");
        }
        Directory.CreateDirectory(activationsFolder);
        List<HistoryTask> tasks;
        int spacing;
        try { tasks = HistoryTasks(root, trial.Tasks, "parent", out spacing); }
        catch (InvalidOperationException e)
        {
            output.WriteLine("error: " + e.Message);
            return 1;
        }
        if (tasks.Count < trial.Tasks)
        {
            output.WriteLine($"{root}: {tasks.Count} usable commits (a code change with its tests); the trial needs {trial.Tasks}. "
                + "The code map reads C#; a repository in another language needs the text-only map, which is not built yet.");
            return 1;
        }
        output.WriteLine($"{manifest.Id} {manifest.Version} on {root}: {tasks.Count} tasks, each at its commit's parent, in memory (no database)");
        var (withRun, grepRun) = (new ArmRun(), new ArmRun());
        foreach (var task in tasks.AsEnumerable().Reverse())
        {
            using var tree = ParentTree.Extract(root, task.Parent);
            var memory = AgentMemory.Extensibility.Ampack.PackMemory.From([], [CodeMapCommand.Read(tree.Path, null).Build("git:" + task.Parent)]);
            var watch = Stopwatch.StartNew();
            var hits = await memory.WalkAsync([.. task.Anchors.Select(a => ContextAnchor.Path(a))], task.Subject).ConfigureAwait(false);
            var ms = watch.Elapsed.TotalMilliseconds;
            // What a host's walk contributor offers: the pack's records, at most its maxItems, as in the host's compile.
            var offered = hits.Where(h => h.Module == manifest.Id).Take(manifest.Traversal!.MaxItems).ToList();
            var files = offered.Where(h => h.Type == CodeMapPack.TestType && h.Via is not null)
                .Select(h => TestFile().Match(h.Text)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var bytes = offered.Sum(h => AgentMemory.Extensibility.Modules.PackTrialRender.Bytes(h.Module, h.Key, h.Text));
            withRun.Add(new TaskOutcome(task, files, files.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase)), ms, ms, bytes));
            grepRun.Add(GrepOne(tree.Path, task));
        }
        withRun.Order(tasks);
        grepRun.Order(tasks);
        var withArm = withRun.Arm(tasks.Count).Arm with { MaximumOffered = manifest.Traversal!.MaxItems, Spacing = spacing };
        var withoutArm = trial.Baseline == "host"
            ? grepRun.Arm(tasks.Count).Arm with { P95Ms = 0, MaximumOffered = GrepTop, Spacing = spacing }
            : new PackTrialMeasurement { Tasks = tasks.Count, Hits = 0, Offered = 0, Correct = 0, P95Ms = 0, PerTask = [.. tasks.Select(_ => new PackTrialTask(0, 0))], MaximumOffered = 0, Spacing = spacing };
        var decision = PackTrialGate.Decide(manifest, withArm, withoutArm, DateTimeOffset.UtcNow) with { History = "parent" };
        var record = PackActivationFolder.Write(activationsFolder, decision, hostKey);
        static double Median(IReadOnlyList<PackTrialTask>? per) =>
            per is { Count: > 0 } ? per.Select(t => (double)(t.OfferedBytes ?? 0)).Order().ElementAt(per.Count / 2) : 0;
        output.WriteLine($"  code map hit {withArm.HitRate:0.000} ({withArm.Hits}/{withArm.Tasks}), precision {withArm.Precision:0.000} ({withArm.Correct}/{withArm.Offered}), median context {Median(withArm.PerTask).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes a task");
        output.WriteLine($"  grep     hit {withoutArm.HitRate:0.000} ({withoutArm.Hits}/{withoutArm.Tasks}), precision {withoutArm.Precision:0.000} ({withoutArm.Correct}/{withoutArm.Offered}), median context {Median(withoutArm.PerTask).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes a task");
        foreach (var check in decision.Checks)
            output.WriteLine($"  {(check.Met ? "met " : "MISS")}  {check.Target} {check.Observed}{(check.Estimate is { } e ? $" (bound; estimate {e})" : "")} (target {check.Required})");
        output.WriteLine($"  decision {(decision.Active ? "ON" : "OFF")}: {decision.Reason}");
        output.WriteLine($"  signed   {record}");
        output.WriteLine("  serve it to your MCP client: ampack serve <codemap.ampack> --policy <trust.json> --print-config");
        return decision.Active ? 0 : 1;
    }

    // A retrieval trial (feature 12): a knowledge pack on the host's recorded questions. The pack is imported once; each task is
    // compiled as of its own time with it and without it (baseline none: the host offers nothing of the pack's without it); a
    // task is a hit when the pack offered one of its expected keys; abstention tasks are counted apart (noHarm); each ability's arm
    // is reported; the decision names the task file by its digest.
    private async Task<int> RetrievalAsync(string? tasksPath, LoadedPack? pack, ModuleManifest manifest, ModuleTrial trial, string activationsFolder, ECDsa hostKey)
    {
        if (tasksPath is null || pack?.Knowledge is not { } knowledge)
        {
            output.WriteLine("error: a retrieval trial runs a knowledge pack (--pack, --trust) on the host's task file (--tasks <file>).");
            return 2;
        }
        if (trial.Baseline == "host")
        {
            output.WriteLine("error: a retrieval trial is measured against nothing here; the host's own retrieval does not hold the pack's keys.");
            return 2;
        }
        RetrievalTaskFile file;
        try { file = RetrievalTaskFile.Read(await File.ReadAllBytesAsync(tasksPath).ConfigureAwait(false)); }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            output.WriteLine("error: " + e.Message);
            return 2;
        }
        var answerable = file.Tasks.Where(t => !t.Abstains).OrderByDescending(t => t.At).Take(trial.Tasks).ToList();
        var abstaining = file.Tasks.Where(t => t.Abstains).OrderByDescending(t => t.At).Take(trial.Tasks).ToList();
        output.WriteLine($"{manifest.Id} {manifest.Version}: {answerable.Count} tasks and {abstaining.Count} abstention tasks from {tasksPath} ({file.Digest})");
        if (answerable.Count < trial.Tasks)
        {
            output.WriteLine($"  not yet: the trial runs {trial.Tasks} tasks; the file holds {answerable.Count} that expect a key.");
            return 1;
        }
        await using var with = await ProviderAsync(m => m.UseNeo4jStore().AddPack(pack)).ConfigureAwait(false);
        await using var without = await ProviderAsync(m => m.UseNeo4jStore()).ConfigureAwait(false);
        var importer = new AgentMemory.Extensibility.Packs.KnowledgePackImporter(
            new AgentMemory.Extensibility.Neo4j.Storage.Neo4jKnowledgePackStore(with.GetRequiredService<INeo4jTransactionRunner>()));
        await importer.ImportAsync(knowledge).ConfigureAwait(false);
        try
        {
            async Task<(List<string> Offered, double Ms)> Offers(IServiceProvider provider, RetrievalTask task)
            {
                await using var scope = provider.CreateAsyncScope();
                var watch = Stopwatch.StartNew();
                var envelope = await scope.ServiceProvider.GetRequiredService<IContextCompiler>().CompileAsync(new ContextRequest
                {
                    SessionId = "pack-trial", Owner = "pack-trial", Query = task.Question, Anchors = [.. task.Anchors], AsOf = task.At,
                }).ConfigureAwait(false);
                var keys = envelope.Sections.SelectMany(s => s.Items).Where(i => i.Provenance?.RecordModuleId == manifest.Id)
                    .Select(i => i.Provenance!.RecordKey).Distinct(StringComparer.Ordinal).ToList();
                return (keys, watch.Elapsed.TotalMilliseconds);
            }
            var rows = new List<(RetrievalTask Task, int Offered, int Right, double Ms, double BaseMs)>();
            foreach (var task in answerable.Concat(abstaining))
            {
                // A first compile of each arm warms the store's caches (review 11-13: only the pack's arm was warmed).
                await Offers(with, task).ConfigureAwait(false);
                await Offers(without, task).ConfigureAwait(false);
                var (offered, ms) = await Offers(with, task).ConfigureAwait(false);
                var (_, baseMs) = await Offers(without, task).ConfigureAwait(false);
                rows.Add((task, offered.Count, offered.Count(k => task.Expected.Contains(k, StringComparer.Ordinal)), ms, baseMs));
            }
            static double P95(IEnumerable<double> times)
            {
                var sorted = times.Order().ToArray();
                return sorted.Length == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1)], 1);
            }
            var tasks = rows.Where(r => !r.Task.Abstains).ToList();
            var quiet = rows.Count(r => r.Task.Abstains && r.Offered == 0);
            PackTrialMeasurement Arm(IReadOnlyList<(RetrievalTask Task, int Offered, int Right, double Ms, double BaseMs)> of) => new()
            {
                Tasks = of.Count, Hits = of.Count(r => r.Right > 0), Offered = of.Sum(r => r.Offered), Correct = of.Sum(r => r.Right), P95Ms = P95(of.Select(r => r.Ms)),
                PerTask = [.. of.Select(r => new PackTrialTask(r.Offered, r.Right))], MaximumOffered = null, // views have no declared cap: bounds take no cap
            };
            var withArm = Arm(tasks) with { Abstentions = abstaining.Count == 0 ? null : abstaining.Count, Quiet = abstaining.Count == 0 ? null : quiet };
            var withoutArm = new PackTrialMeasurement
            {
                Tasks = tasks.Count, Hits = 0, Offered = 0, Correct = 0, P95Ms = P95(tasks.Select(r => r.BaseMs)), PerTask = [.. tasks.Select(_ => new PackTrialTask(0, 0))],
                MaximumOffered = 0, Abstentions = abstaining.Count == 0 ? null : abstaining.Count, Quiet = abstaining.Count == 0 ? null : abstaining.Count,
            };
            var abilities = tasks.Where(r => r.Task.Ability is not null).GroupBy(r => r.Task.Ability!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Arm([.. g]), StringComparer.Ordinal);
            var decision = PackTrialGate.Decide(manifest, withArm, withoutArm, DateTimeOffset.UtcNow, pack.Digest) with
            {
                History = "tasks", TaskFile = file.Digest, Abilities = abilities.Count == 0 ? null : abilities,
            };
            var record = PackActivationFolder.Write(activationsFolder, decision, hostKey);
            output.WriteLine($"  with     hit {withArm.HitRate:0.000} ({withArm.Hits}/{withArm.Tasks}), precision {withArm.Precision:0.000} ({withArm.Correct}/{withArm.Offered})"
                + (abstaining.Count == 0 ? "" : $", quiet on {quiet}/{abstaining.Count} abstention tasks"));
            foreach (var (ability, arm) in abilities.OrderBy(a => a.Key, StringComparer.Ordinal))
                output.WriteLine($"  {ability,-13} hit {arm.HitRate:0.000} ({arm.Hits}/{arm.Tasks})");
            foreach (var check in decision.Checks)
                output.WriteLine($"  {(check.Met ? "met " : "MISS")}  {check.Target} {check.Observed} (target {check.Required})");
            output.WriteLine($"  decision {(decision.Active ? "ON" : "OFF")}: {decision.Reason}");
            output.WriteLine($"  signed   {record}");
            return decision.Active ? 0 : 1;
        }
        finally { await importer.RemoveAsync(knowledge.Manifest).ConfigureAwait(false); }
    }

    // A shadow trial (feature 11, R2): the pack ran dark on live turns; each turn is labelled by the code-history rule, the tests the
    // next commit after it that touched one of its anchor paths changed, and the latest of as many labelled turns as the trial runs
    // are its tasks, against nothing (the pack offered nothing to those turns). The same exact decision, recorded as "shadow".
    private int DecideFromShadow(string shadowPath, string root, ModuleManifest manifest, ModuleTrial trial, string? packDigest,
        string activationsFolder, ECDsa hostKey)
    {
        if (trial.Baseline == "host")
        {
            output.WriteLine("error: a shadow trial is measured against nothing; this trial's baseline is the host's own retrieval, which the log does not hold.");
            return 2;
        }
        IReadOnlyList<ShadowTurn> turns;
        // Only turns this exact pack ran dark on (review 11-13, H4), each turn once whatever contributors it had.
        try
        {
            turns = [.. ShadowLogFile.Read(shadowPath).Where(t => t.ModuleId == manifest.Id && t.PackDigest == packDigest)
                .GroupBy(t => t.TurnId, StringComparer.Ordinal)
                .Select(g => g.First() with { Offers = [.. g.SelectMany(t => t.Offers)], Milliseconds = g.Sum(t => t.Milliseconds) })];
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            output.WriteLine("error: " + e.Message);
            return 2;
        }
        var commits = HistoryTasks(root, PackTrialGate.HistoryWindow, "parent", out _);
        var labelled = turns.Select(t =>
        {
            var paths = t.Anchors.Where(a => a.StartsWith("path:", StringComparison.Ordinal)).Select(a => a[5..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (Turn: t, Outcome: commits.Where(c => c.At > t.At && c.Anchors.Any(paths.Contains)).MinBy(c => c.At));
        }).Where(l => l.Outcome is not null)
            // One task per labelling commit, its last turn before it (review 11-13: many turns sharing one label are not independent).
            .GroupBy(l => l.Outcome!.Commit, StringComparer.Ordinal).Select(g => g.MaxBy(l => l.Turn.At))
            .OrderByDescending(l => l.Turn.At).Take(trial.Tasks).ToList();
        output.WriteLine($"{manifest.Id} {manifest.Version}: {turns.Count} shadow turns, {labelled.Count} labelled by a later commit (a trial runs {trial.Tasks})");
        if (labelled.Count < trial.Tasks)
        {
            output.WriteLine($"  not yet: {trial.Tasks - labelled.Count} more labelled turns are needed.");
            return 1;
        }
        var per = labelled.Select(l =>
        {
            var offered = l.Turn.Offers.Select(o => o.Locator).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new PackTrialTask(offered.Count, offered.Count(f => l.Outcome!.Tests.Contains(f, StringComparer.OrdinalIgnoreCase)));
        }).ToList();
        var withArm = new PackTrialMeasurement
        {
            // The pack's own time on each turn, as the shadow contributor measured it (review 11-13: both arms were 0 ms).
            Tasks = per.Count, Hits = per.Count(t => t.Correct > 0), Offered = per.Sum(t => t.Offered), Correct = per.Sum(t => t.Correct),
            P95Ms = P95Of(labelled.Select(l => l.Turn.Milliseconds)), PerTask = per, MaximumOffered = manifest.Traversal?.MaxItems,
        };
        var withoutArm = new PackTrialMeasurement
        {
            Tasks = per.Count, Hits = 0, Offered = 0, Correct = 0, P95Ms = 0, PerTask = [.. per.Select(_ => new PackTrialTask(0, 0))], MaximumOffered = 0,
        };
        var decision = PackTrialGate.Decide(manifest, withArm, withoutArm, DateTimeOffset.UtcNow, packDigest) with { History = "shadow" };
        var record = PackActivationFolder.Write(activationsFolder, decision, hostKey);
        output.WriteLine($"  shadow   hit {withArm.HitRate:0.000} ({withArm.Hits}/{withArm.Tasks}), precision {withArm.Precision:0.000} ({withArm.Correct}/{withArm.Offered})");
        output.WriteLine($"  decision {(decision.Active ? "ON" : "OFF")}: {decision.Reason}");
        output.WriteLine($"  signed   {record}");
        return decision.Active ? 0 : 1;
    }

    private static double P95Of(IEnumerable<double> times)
    {
        var sorted = times.Order().ToArray();
        return sorted.Length == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1)], 1);
    }

    // One task through one arm: the test files it offered, how many were right, and how long the compile took.
    private static async Task<TaskOutcome> MeasureOneAsync(IServiceProvider provider, HistoryTask task)
    {
        var (envelope, ms) = await CompileAsync(provider, task).ConfigureAwait(false);
        // What the walk offered (it says how it reached each item), at most the pack's maxItems: the arm's cap (review 6: a test
        // record another contributor added counted too, and could exceed the cap the bounds assume).
        var files = envelope.Sections.SelectMany(s => s.Items)
            .Where(i => i.Provenance?.RecordModuleId == CodeMapPack.Id && i.Provenance.RecordType == CodeMapPack.TestType && i.Provenance.Via is not null)
            .Select(i => TestFile().Match(i.Text)).Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Everything the pack put into the context this task, framed as the model reads it (feature 05).
        var items = envelope.Sections.SelectMany(s => s.Items.Select(i => (Section: s, Item: i))).Where(x => x.Item.Provenance?.RecordModuleId == CodeMapPack.Id).ToList();
        var bytes = items.Sum(x => PackTrialRender.Bytes(x.Section, x.Item));
        // Where those bytes go, by record type and whether the walk reached the record or it is an anchor's own (feature 05).
        var parts = items.GroupBy(x => $"{x.Item.Provenance!.RecordType}{(x.Item.Provenance.Via is null ? " (anchor)" : "")}")
            .ToDictionary(g => g.Key, g => new { items = g.Count(), bytes = g.Sum(x => PackTrialRender.Bytes(x.Section, x.Item)),
                textBytes = g.Sum(x => (long)System.Text.Encoding.UTF8.GetByteCount(x.Item.Text)) });
        // What each part offered (feature 11, R3): by the first relation of each offer's reason path, an anchor's own record apart.
        var byPart = envelope.Sections.SelectMany(s => s.Items)
            .Where(i => i.Provenance?.RecordModuleId == CodeMapPack.Id && i.Provenance.RecordType == CodeMapPack.TestType && i.Provenance.Via is not null)
            .GroupBy(i => i.Provenance!.ViaRelation ?? PackTrialGate.AnchorPart, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var offered = g.Select(i => TestFile().Match(i.Text)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return (Offered: offered.Count, Right: offered.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase)));
            }, StringComparer.Ordinal);
        return new TaskOutcome(task, files, files.Count(f => task.Tests.Contains(f, StringComparer.OrdinalIgnoreCase)), ms, ms, bytes) { Parts = parts, ByPart = byPart };
    }

    private sealed record TaskOutcome(HistoryTask Task, IReadOnlyList<string> Offered, int Right, double Milliseconds, double? Timed, long Bytes)
    {
        public IReadOnlyDictionary<string, (int Offered, int Right)> ByPart { get; init; } = new Dictionary<string, (int, int)>();
        public object? Parts { get; init; }
    }

    // The outcomes of one arm, task by task, and the measurement they make.
    private sealed class ArmRun
    {
        private List<TaskOutcome> _outcomes = [];

        public void Add(TaskOutcome outcome) => _outcomes.Add(outcome);

        // Each part's arm on the same tasks (feature 11): what that part offered and got right, task by task; the walk's time and cap
        // are the whole arm's.
        public IReadOnlyDictionary<string, PackTrialMeasurement> Parts(int tasks, PackTrialMeasurement whole) =>
            _outcomes.SelectMany(o => o.ByPart.Keys).Distinct(StringComparer.Ordinal).ToDictionary(part => part, part =>
            {
                var per = _outcomes.Select(o => o.ByPart.TryGetValue(part, out var x) ? x : (Offered: 0, Right: 0)).ToList();
                return whole with
                {
                    Tasks = tasks, Hits = per.Count(x => x.Right > 0), Offered = per.Sum(x => x.Offered), Correct = per.Sum(x => x.Right),
                    PerTask = [.. per.Select(x => new PackTrialTask(x.Offered, x.Right))],
                };
            }, StringComparer.Ordinal);

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
                P95Ms = Math.Round(p95, 1), PerTask = [.. _outcomes.Select(o => new PackTrialTask(o.Offered.Count, o.Right) { OfferedBytes = o.Bytes })],
            }, [.. _outcomes.Select(o => (object)new
            {
                o.Task.Commit, parent = o.Task.Parent[..10], o.Task.Subject, o.Task.Anchors, o.Task.Tests, offered = o.Offered, hit = o.Right > 0, bytes = o.Bytes, parts = o.Parts,
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
                // The packages each project restores (review 3): the map reads obj/project.assets.json to bind calls into package
                // types, and a parent tree has no obj/, so its map had fewer links than the host's. HEAD's restore stands in for
                // the parent's: an approximation, in which a package added or removed between the two reads as at HEAD.
                foreach (var project in Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
                {
                    var projectFolder = System.IO.Path.GetDirectoryName(project)!;
                    var assets = System.IO.Path.Combine(repo, System.IO.Path.GetRelativePath(folder, projectFolder), "obj", "project.assets.json");
                    if (!File.Exists(assets)) continue;
                    Directory.CreateDirectory(System.IO.Path.Combine(projectFolder, "obj"));
                    File.Copy(assets, System.IO.Path.Combine(projectFolder, "obj", "project.assets.json"), overwrite: true);
                }
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
            // As of the task's own time (feature 07, R-TRIAL-058): a record true only later never answers it.
            AsOf = task.At,
        }).ConfigureAwait(false);
        return (envelope, watch.Elapsed.TotalMilliseconds);
    }

    private sealed record HistoryTask(string Commit, string Parent, string Subject, IReadOnlyList<string> Anchors, IReadOnlyList<string> Tests)
    {
        /// <summary>When the change was made, its commit time: the world time the task is read at (feature 07).</summary>
        public DateTimeOffset? At { get; init; }
    }

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
    private static List<HistoryTask> HistoryTasks(string repo, int count, string history, out int spacing)
    {
        spacing = 1;
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        // The history window the gate's intake holds every trial to (review 7, G5): one constant for both.
        foreach (var arg in new[] { "-C", repo, "log", "--no-merges", "-n", PackTrialGate.HistoryWindow.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--format=%x1e%H%x1f%P%x1f%cI%x1f%s", "--name-status" }) start.ArgumentList.Add(arg);
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
            tasks.Add(new HistoryTask(head[0][..10], head[1].Split(' ')[0], head.Length > 3 ? head[3] : "", code, tests)
            {
                // Fails closed (review F07, M4): a task whose time cannot be read is not read at now instead.
                At = head.Length > 2 && DateTimeOffset.TryParse(head[2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                    ? at : throw new InvalidOperationException($"git log gave no commit time for {head[0][..10]}: a trial reads each task as of its own time."),
            });
        }
        // Spread over the history, not the most recent run of commits (review 6: consecutive commits are not independent draws;
        // the code map's own forty had a lag-1 autocorrelation of 0.22 in hits, and bursts of related changes pass a bound far
        // more often than its level): every k-th eligible commit of the last 2,000, k the most that still gives the count.
        // The spacing travels with the measurement: closer than a burst, the bounds cluster the tasks (review 6, H4).
        var stride = spacing = Math.Max(1, tasks.Count / Math.Max(1, count));
        if (tasks.Count <= count) return tasks;
        return [.. Enumerable.Range(0, count).Select(i => tasks[i * stride])];
    }

    // The code map renders a test as "Test {name} ({file})"; the traversal contributor then adds "(reached from …)".
    [GeneratedRegex(@"\(([^()]+\.cs)\)")]
    private static partial Regex TestFile();
}
