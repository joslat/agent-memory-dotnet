#pragma warning disable AMEXT001 // The work pack is an Extensibility preview pack.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Packs.Work;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// The work pack (CODING-hierarchy). <c>work scan --ledger &lt;tasks.json&gt; --root &lt;repo&gt;</c> reads the task ledger and the
/// repository's commits and reports what it found, offline. <c>work import ...</c> writes the work items into the configured store,
/// each linked to the code map files its commits changed: import the code map first; links to files it does not hold are left out.
/// </summary>
internal sealed class WorkCommand(TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Scan(string? ledger, string? root)
    {
        if (ledger is null || root is null) return Usage("work scan needs --ledger <tasks.json> and --root <repository>.");
        var snapshot = Read(Path.GetFullPath(ledger), Path.GetFullPath(root), null);
        var problems = snapshot.Problems();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            source = snapshot.Source, items = snapshot.Records.Count,
            withCommits = snapshot.Records.Count(r => r.Properties["commits"].GetInt32() > 0),
            links = snapshot.Records.Sum(r => r.Links.Count),
            byStatus = snapshot.Records.GroupBy(r => r.Properties["status"].GetString()).ToDictionary(g => g.Key!, g => g.Count()),
            mostLinked = snapshot.Records.OrderByDescending(r => r.Links.Count).Take(8).Select(r => r.Key + " (" + r.Links.Count + ")"), problems,
        }, Json));
        return problems.Count == 0 ? 0 : 1;
    }

    public async Task<int> ImportAsync(INeo4jTransactionRunner runner, string? ledger, string? root, string? source)
    {
        if (ledger is null || root is null) return Usage("work import needs --ledger <tasks.json> and --root <repository>.");
        var snapshot = Read(Path.GetFullPath(ledger), Path.GetFullPath(root), source);
        var result = await new Neo4jModuleSnapshotStore(runner).ImportAsync(snapshot, DateTimeOffset.UtcNow).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            result.ModuleId, result.Source, result.Created, result.Changed, result.Unchanged, result.Closed, result.LinksLeftOut,
            items = snapshot.Records.Count, links = snapshot.Records.Sum(r => r.Links.Count),
        }, Json));
        return 0;
    }

    // The ledger's items; the repository's commits (no merges), each with the files it changed that are still tracked C# files,
    // which is what a Roslyn code map holds.
    internal static Extensibility.Packs.ModuleSnapshot Read(string ledger, string root, string? source)
    {
        var json = File.ReadAllText(ledger);
        var tracked = (Git(root, "ls-files", "-z") ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        var log = Git(root, "log", "--no-merges", "--format=%x1e%H%x1f%s", "--name-only") ?? "";
        var commits = log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(entry =>
        {
            var lines = entry.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var fields = lines[0].Split('\x1f');
            return new WorkCommit(fields[0], fields.Length > 1 ? fields[1] : "", lines.Skip(1).ToList());
        }).ToList();
        var head = Git(root, "rev-parse", "HEAD")?.Trim();
        var ledgerHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..12].ToLowerInvariant();
        return WorkLedgerReader.Read(json, Path.GetFileName(ledger), commits,
            source ?? (head is { Length: 40 or 64 } ? $"git:{head} ledger:{ledgerHash}" : "scan ledger:" + ledgerHash), tracked);
    }

    private static string? Git(string root, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private int Usage(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 2;
    }
}
