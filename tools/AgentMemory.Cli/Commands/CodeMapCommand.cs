#pragma warning disable AMEXT001 // The code map is an Extensibility preview pack.
using System.Diagnostics;
using System.Text.Json;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.CodeMap.Roslyn;
using AgentMemory.Packs.CodeMap.Scip;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// The code map (coding packs CMP-2a). <c>codemap scan --root &lt;repo&gt;</c> reads a C# repository with Roslyn and reports what it
/// found, offline. <c>codemap import --root &lt;repo&gt; [--source git:&lt;sha&gt;]</c> writes the snapshot into the configured store:
/// only what changed since the last import, closing what is gone. The source defaults to the repository's HEAD commit.
/// <c>codemap scan --output map.jsonl</c> also writes the records, one per line, for reading the map without a store.
/// <c>--scip index.scip[,other.scip]</c> adds SCIP indexes (CMP-2b: TypeScript, Python, Java, Go, Rust...) to the same map; without
/// <c>--root</c> the map is the indexes alone, and import needs <c>--source</c>.
/// <c>codemap pack --root &lt;repo&gt; --id &lt;id&gt; --version &lt;v&gt; --key &lt;private.pem&gt; --out &lt;file.ampack&gt;</c> shares the map as a
/// signed snapshot pack (pack format v1.2 §3), offline; <c>codemap import --pack &lt;file.ampack&gt; --trust &lt;trust.json&gt;</c>
/// imports one, verified, instead of reading a repository.
/// </summary>
internal sealed class CodeMapCommand(TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Scan(string? root, string? outputPath = null, string? scip = null)
    {
        if (root is null && scip is null) return Usage("codemap scan needs --root <repository>, --scip <index.scip>, or both.");
        var watch = Stopwatch.StartNew();
        var builder = Read(root, scip);
        var snapshot = builder.Build((root is null ? null : Head(root)) ?? "scan");
        var (projects, files, symbols, tests) = builder.Counts;
        var problems = snapshot.Problems();
        if (outputPath is not null)
            File.WriteAllLines(outputPath, snapshot.Records.Select(r => JsonSerializer.Serialize(new
            {
                type = r.Type, key = r.Key, properties = r.Properties, links = r.Links.Select(l => new { relation = l.Relation, target = l.Target }),
            })));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            source = snapshot.Source, projects, files, symbols, tests, records = snapshot.Records.Count, digest = snapshot.Digest,
            testsLinked = snapshot.Records.Count(r => r.Type == "CodemapTest" && r.Links.Any(l => l.Relation == "CODEMAP_TESTS")),
            seconds = Math.Round(watch.Elapsed.TotalSeconds, 1), problems,
        }, Json));
        return problems.Count == 0 ? 0 : 1;
    }

    public int Pack(string? root, string? scip, string? id, string? version, string? keyPath, string? outPath, string? repository)
    {
        if (root is null && scip is null) return Usage("codemap pack needs --root <repository>, --scip <index.scip>, or both.");
        if (id is null || version is null || keyPath is null || outPath is null)
            return Usage("codemap pack needs --id <reverse.dns.id> --version <semver> --key <private.pem> --out <file.ampack>.");
        if (!File.Exists(keyPath)) return Usage($"there is no key at {keyPath} (ampack keygen makes one).");
        var watch = Stopwatch.StartNew();
        var head = root is null ? null : Head(root);
        var snapshot = Read(root, scip).Build(head ?? "pack");
        if (snapshot.Problems() is { Count: > 0 } problems)
        {
            output.WriteLine(JsonSerializer.Serialize(new { records = snapshot.Records.Count, problems }, Json));
            return 1;
        }
        byte[] archive;
        try
        {
            archive = PackAuthoring.Build(PackAuthoring.FromSnapshot(snapshot, id, version, repository, head?["git:".Length..]), File.ReadAllText(keyPath));
        }
        catch (PackLoadException e)
        {
            output.WriteLine(JsonSerializer.Serialize(new { problems = e.Problems }, Json));
            return 1;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, archive);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            id, version, commit = head, records = snapshot.Records.Count, snapshotDigest = snapshot.Digest, bytes = archive.Length, wrote = outPath,
            seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
        }, Json));
        return 0;
    }

    public async Task<int> ImportAsync(INeo4jTransactionRunner runner, string? root, string? source, string? scip = null, string? packPath = null,
        string? trustPath = null, bool rebuildIfStale = false)
    {
        if (packPath is not null) return await ImportPackAsync(runner, packPath, trustPath, root, rebuildIfStale).ConfigureAwait(false);
        if (root is null && scip is null) return Usage("codemap import needs --root <repository>, --scip <index.scip>, --pack <file.ampack>, or one of them.");
        var watch = Stopwatch.StartNew();
        var snapshot = Read(root, scip).Build(source ?? (root is null ? null : Head(root)) ?? throw new InvalidOperationException(
            "codemap import needs --source when there is no Git repository to take the commit from."));
        var read = watch.Elapsed;
        var result = await new Neo4jModuleSnapshotStore(runner).ImportAsync(snapshot, DateTimeOffset.UtcNow).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            result.ModuleId, result.Source, result.Created, result.Changed, result.Unchanged, result.Closed, records = snapshot.Records.Count,
            digest = snapshot.Digest, readSeconds = Math.Round(read.TotalSeconds, 1), importSeconds = Math.Round((watch.Elapsed - read).TotalSeconds, 1),
        }, Json));
        return 0;
    }

    // A shared snapshot pack: verified against the host's trust, its records checked against this host's code map, then imported.
    // With --root, its freshness against the repository's commit is reported (PACK-staleness); with --rebuild-if-stale, a stale
    // pack is set aside and the repository itself is read and imported instead.
    private async Task<int> ImportPackAsync(INeo4jTransactionRunner runner, string packPath, string? trustPath, string? root, bool rebuildIfStale)
    {
        if (rebuildIfStale && root is null) return Usage("codemap import --rebuild-if-stale needs --root <repository> to rebuild from.");
        if (trustPath is null || !File.Exists(trustPath)) return Usage("codemap import --pack needs --trust <trust.json>.");
        var watch = Stopwatch.StartNew();
        ModuleSnapshot snapshot;
        LoadedPack pack;
        try
        {
            pack = PackLoader.Load(packPath, PackTrust.Load(trustPath));
            snapshot = pack.ToSnapshot(CodeMapPack.Manifest);
        }
        catch (PackLoadException e)
        {
            output.WriteLine(JsonSerializer.Serialize(new { refused = packPath, problems = e.Problems }, Json));
            return 1;
        }
        SnapshotFreshness? freshness = null;
        int? behind = null;
        if (root is not null && Head(root) is { } head)
        {
            freshness = pack.Freshness(head);
            if (freshness.BuiltAt is { } built) behind = CommitsSince(root, built);
        }
        var rebuilt = false;
        if (freshness?.Fresh == false && rebuildIfStale)
        {
            snapshot = Read(root, null).Build(Head(root!)!);
            rebuilt = true;
        }
        var read = watch.Elapsed;
        // The snapshot pack's verified erasures first (review F08, H3): what they name never reaches the store. A snapshot rebuilt
        // from the repository is not the pack's, so they do not apply to it.
        var result = await new Neo4jModuleSnapshotStore(runner).ImportAsync(snapshot, rebuilt ? [] : pack.Erasures, DateTimeOffset.UtcNow).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            pack = pack.Id, packVersion = pack.Version, packDigest = pack.Digest,
            freshness = freshness is null ? null : new { freshness.BuiltAt, freshness.Head, freshness.Fresh, behind, freshness.Detail },
            rebuilt, result.ModuleId, result.Source, result.Created, result.Changed,
            result.Unchanged, result.Closed, records = snapshot.Records.Count, digest = snapshot.Digest, readSeconds = Math.Round(read.TotalSeconds, 1),
            importSeconds = Math.Round((watch.Elapsed - read).TotalSeconds, 1),
        }, Json));
        return 0;
    }

    // How many commits the repository's head is past <commit>, or null when the repository does not have that commit.
    private static int? CommitsSince(string root, string commit)
    {
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-C", root, "rev-list", "--count", commit + "..HEAD" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var count = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && int.TryParse(count, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    // C# from the repository with Roslyn, then each SCIP index into the same map.
    internal static CodeMapBuilder Read(string? root, string? scip)
    {
        var builder = root is null ? new CodeMapBuilder() : RoslynCodeMapReader.Read(Path.GetFullPath(root));
        foreach (var index in (scip ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            ScipCodeMapReader.Read(index, builder);
        return builder;
    }

    private static string? Head(string root)
    {
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-C", root, "rev-parse", "HEAD" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var hash = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && hash.Length is 40 or 64 ? "git:" + hash : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private int Usage(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 2;
    }
}
