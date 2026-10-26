#pragma warning disable AMEXT001 // The code map is an Extensibility preview pack.
using System.Diagnostics;
using System.Text.Json;
using AgentMemory.Extensibility.Neo4j.Storage;
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

    public async Task<int> ImportAsync(INeo4jTransactionRunner runner, string? root, string? source, string? scip = null)
    {
        if (root is null && scip is null) return Usage("codemap import needs --root <repository>, --scip <index.scip>, or both.");
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

    // C# from the repository with Roslyn, then each SCIP index into the same map.
    private static CodeMapBuilder Read(string? root, string? scip)
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
