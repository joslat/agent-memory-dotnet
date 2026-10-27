#pragma warning disable AMEXT001 // The product intent pack is an Extensibility preview pack.
using System.Diagnostics;
using System.Text.Json;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Packs.Intent;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// The product intent pack (coding packs CMP-6). <c>intent scan --root &lt;repo&gt;</c> reads the repository's requirement
/// documents, Gherkin features, OpenSLO objectives and ideas and reports what it found, offline. <c>intent import --root
/// &lt;repo&gt; [--source git:&lt;sha&gt;]</c> writes them into the configured store: only what changed, closing what is gone.
/// </summary>
internal sealed class IntentCommand(TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Scan(string? root)
    {
        if (root is null) return Usage("intent scan needs --root <repository>.");
        var snapshot = Read(Path.GetFullPath(root), null, out var files, out var skipped);
        var problems = snapshot.Problems();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            source = snapshot.Source, files, skipped,
            byType = snapshot.Records.GroupBy(r => r.Type).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            links = snapshot.Records.Sum(r => r.Links.Count), problems,
        }, Json));
        return problems.Count == 0 ? 0 : 1;
    }

    public async Task<int> ImportAsync(INeo4jTransactionRunner runner, string? root, string? source)
    {
        if (root is null) return Usage("intent import needs --root <repository>.");
        var snapshot = Read(Path.GetFullPath(root), source, out _, out var skipped);
        var result = await new Neo4jModuleSnapshotStore(runner).ImportAsync(snapshot, DateTimeOffset.UtcNow).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            result.ModuleId, result.Source, result.Created, result.Changed, result.Unchanged, result.Closed, records = snapshot.Records.Count, skipped,
        }, Json));
        return 0;
    }

    private static Extensibility.Packs.ModuleSnapshot Read(string root, string? source, out IReadOnlyList<string> files, out IReadOnlyList<string> skipped)
    {
        var listed = Git(root, "ls-files", "-z") is { } text ? text.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        files = IntentReader.IntentFiles(listed);
        var head = Git(root, "rev-parse", "HEAD")?.Trim();
        return IntentReader.Read(root, files, source ?? (head is { Length: 40 or 64 } ? "git:" + head : "scan"), out skipped);
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
