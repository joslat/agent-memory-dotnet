#pragma warning disable AMEXT001 // The decisions pack is an Extensibility preview pack.
using System.Diagnostics;
using System.Text.Json;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Packs.Decisions;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// The decisions pack (coding packs CMP-3). <c>decisions scan --root &lt;repo&gt;</c> reads the repository's ADR files and Squad's
/// decision log and reports what it found, offline. <c>decisions import --root &lt;repo&gt; [--source git:&lt;sha&gt;]</c> writes them
/// into the configured store: only what changed, closing decisions whose entries are gone.
/// </summary>
internal sealed class DecisionsCommand(TextWriter output)
{
    private static readonly string[] LocalDecisionFiles = [".squad/decisions.md"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Scan(string? root)
    {
        if (root is null) return Usage("decisions scan needs --root <repository>.");
        var snapshot = Read(Path.GetFullPath(root), null, out var sources);
        var problems = snapshot.Problems();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            source = snapshot.Source, files = sources, decisions = snapshot.Records.Count,
            applying = snapshot.Records.Count(r => r.Properties.ContainsKey("appliesTo")),
            byStatus = snapshot.Records.GroupBy(r => r.Properties["status"].GetString()).ToDictionary(g => g.Key!, g => g.Count()),
            titles = snapshot.Records.Take(12).Select(r => r.Properties["title"].GetString()), problems,
        }, Json));
        return problems.Count == 0 ? 0 : 1;
    }

    public async Task<int> ImportAsync(INeo4jTransactionRunner runner, string? root, string? source)
    {
        if (root is null) return Usage("decisions import needs --root <repository>.");
        var snapshot = Read(Path.GetFullPath(root), source, out _);
        var result = await new Neo4jModuleSnapshotStore(runner).ImportAsync(snapshot, DateTimeOffset.UtcNow).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            result.ModuleId, result.Source, result.Created, result.Changed, result.Unchanged, result.Closed, decisions = snapshot.Records.Count,
        }, Json));
        return 0;
    }

    internal static Extensibility.Packs.ModuleSnapshot Read(string root, string? source, out IReadOnlyList<string> sources, bool reviewed = true)
    {
        var files = Git(root, "ls-files", "-z") is { } listed ? listed.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        // Squad keeps its team state local (often git-ignored); its decision log counts when it is there.
        string[] local = [.. LocalDecisionFiles.Where(f => File.Exists(Path.Combine(root, f)) && !files.Contains(f, StringComparer.Ordinal))];
        sources = DecisionsReader.DecisionFiles(files.Concat(local));
        var code = CodeIndex.Scan(root, [.. files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))]);
        var head = Git(root, "rev-parse", "HEAD")?.Trim();
        var snapshot = DecisionsReader.Read(root, sources, code, source ?? (head is { Length: 40 or 64 } ? "git:" + head : "scan"));
        return reviewed ? OnboardCommand.Reviewed(snapshot, root) : snapshot;
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
