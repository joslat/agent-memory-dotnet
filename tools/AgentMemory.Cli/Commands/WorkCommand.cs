#pragma warning disable AMEXT001 // The work pack is an Extensibility preview pack.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Packs.Work;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// The work pack (CODING-hierarchy, CMP-8). <c>work scan --ledger &lt;tasks.json&gt; --root &lt;repo&gt;</c> reads the task ledger
/// (or, with <c>--github-issues &lt;issues.json&gt; [--github-milestones &lt;milestones.json&gt;]</c>, GitHub's exports instead), the
/// repository's commits and its release tags, and reports what it found, offline. <c>work import ...</c> writes the work items,
/// their waves and releases into the configured store, each item linked to the code map files its commits changed: import the
/// code map first; links to files it does not hold are left out.
/// </summary>
internal sealed class WorkCommand(TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Scan(string? ledger, string? root, string? issues = null, string? milestones = null)
    {
        if (ledger is null && issues is null || root is null) return Usage("work scan needs --ledger <tasks.json> (or --github-issues <issues.json>) and --root <repository>.");
        var snapshot = Read(ledger, Path.GetFullPath(root), null, issues, milestones);
        var problems = snapshot.Problems();
        var items = snapshot.Records.Where(r => r.Type == WorkPack.ItemType).ToList();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            source = snapshot.Source, items = items.Count,
            withCommits = items.Count(r => r.Properties["commits"].GetInt32() > 0),
            waves = snapshot.Records.Count(r => r.Type == WorkPack.WaveType), releases = snapshot.Records.Count(r => r.Type == WorkPack.ReleaseType),
            links = snapshot.Records.Sum(r => r.Links.Count),
            byStatus = items.GroupBy(r => r.Properties["status"].GetString()).ToDictionary(g => g.Key!, g => g.Count()),
            shipped = items.Count(r => r.Links.Any(l => l.Relation == WorkPack.ReleasedIn)), blocked = items.Count(r => r.Links.Any(l => l.Relation == WorkPack.BlockedBy)),
            mostLinked = items.OrderByDescending(r => r.Links.Count).Take(8).Select(r => r.Key + " (" + r.Links.Count + ")"), problems,
        }, Json));
        return problems.Count == 0 ? 0 : 1;
    }

    public async Task<int> ImportAsync(INeo4jTransactionRunner runner, string? ledger, string? root, string? source, string? issues = null, string? milestones = null)
    {
        if (ledger is null && issues is null || root is null) return Usage("work import needs --ledger <tasks.json> (or --github-issues <issues.json>) and --root <repository>.");
        var snapshot = Read(ledger, Path.GetFullPath(root), source, issues, milestones);
        var result = await new Neo4jModuleSnapshotStore(runner).ImportAsync(snapshot, DateTimeOffset.UtcNow).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            result.ModuleId, result.Source, result.Created, result.Changed, result.Unchanged, result.Closed, result.LinksLeftOut,
            records = snapshot.Records.Count, links = snapshot.Records.Sum(r => r.Links.Count),
        }, Json));
        return 0;
    }

    // The ledger's (or GitHub's) items; the repository's commits (no merges), each with the files it changed that are still
    // tracked C# files, which is what a Roslyn code map holds; and its release tags, each with the commits it shipped.
    internal static Extensibility.Packs.ModuleSnapshot Read(string? ledger, string root, string? source, string? issues = null, string? milestones = null)
    {
        var tracked = (Git(root, "ls-files", "-z") ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        var log = Git(root, "log", "--no-merges", "--format=%x1e%H%x1f%s", "--name-only") ?? "";
        var commits = log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(entry =>
        {
            var lines = entry.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var fields = lines[0].Split('\x1f');
            return new WorkCommit(fields[0], fields.Length > 1 ? fields[1] : "", lines.Skip(1).ToList());
        }).ToList();
        var releases = Releases(root);
        var head = Git(root, "rev-parse", "HEAD")?.Trim();
        if (issues is not null)
        {
            var issuesJson = File.ReadAllText(Path.GetFullPath(issues));
            var milestonesJson = milestones is null ? null : File.ReadAllText(Path.GetFullPath(milestones));
            var exportHash = Hash(issuesJson + milestonesJson);
            return GitHubWorkReader.Read(issuesJson, milestonesJson, commits,
                source ?? (head is { Length: 40 or 64 } ? $"git:{head} github:{exportHash}" : "scan github:" + exportHash), tracked, releases);
        }
        var json = File.ReadAllText(Path.GetFullPath(ledger!));
        var ledgerHash = Hash(json);
        return WorkLedgerReader.Read(json, Path.GetFileName(ledger!), commits,
            source ?? (head is { Length: 40 or 64 } ? $"git:{head} ledger:{ledgerHash}" : "scan ledger:" + ledgerHash), tracked, releases);
    }

    // Every tag, oldest first, with its commit and the commits it shipped since the tag before it.
    private static List<WorkRelease> Releases(string root)
    {
        var releases = new List<WorkRelease>();
        string? previous = null;
        foreach (var line in (Git(root, "for-each-ref", "--sort=creatordate", "--format=%(refname:short)%1f%(creatordate:iso-strict)", "refs/tags") ?? "")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\x1f');
            var tag = fields[0];
            var commit = Git(root, "rev-parse", tag + "^{commit}")?.Trim();
            if (commit is not { Length: 40 or 64 }) continue;
            var range = previous is null ? commit : $"{previous}..{commit}";
            var shipped = (Git(root, "rev-list", "--no-merges", range) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            releases.Add(new WorkRelease(tag, fields.Length > 1 ? fields[1] : null, commit, shipped));
            previous = commit;
        }
        return releases;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

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
