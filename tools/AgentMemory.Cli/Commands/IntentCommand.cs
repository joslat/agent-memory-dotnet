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
/// With <c>--ledger &lt;tasks.json&gt;</c> instead of <c>--root</c>, a task ledger is read as intent (CO-2d), each row a
/// requirement; import the code map, then this, then <c>work import --ledger ... --ledger-as-intent</c>.
/// </summary>
internal sealed class IntentCommand(TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Scan(string? root, string? ledger = null)
    {
        if (root is null && ledger is null) return Usage("intent scan needs --root <repository> (or --ledger <tasks.json>, the ledger read as intent).");
        IReadOnlyList<string> files = [], skipped = [];
        var snapshot = ledger is not null ? FromLedger(ledger, null) : Read(Path.GetFullPath(root!), null, out files, out skipped);
        var problems = snapshot.Problems();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            source = snapshot.Source, files, skipped,
            byType = snapshot.Records.GroupBy(r => r.Type).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            links = snapshot.Records.Sum(r => r.Links.Count), problems,
        }, Json));
        return problems.Count == 0 ? 0 : 1;
    }

    public async Task<int> ImportAsync(INeo4jTransactionRunner runner, string? root, string? source, string? ledger = null)
    {
        if (root is null && ledger is null) return Usage("intent import needs --root <repository> (or --ledger <tasks.json>, the ledger read as intent).");
        IReadOnlyList<string> skipped = [];
        var snapshot = ledger is not null ? FromLedger(ledger, source) : Read(Path.GetFullPath(root!), source, out _, out skipped);
        var result = await new Neo4jModuleSnapshotStore(runner).ImportAsync(snapshot, DateTimeOffset.UtcNow).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            result.ModuleId, result.Source, result.Created, result.Changed, result.Unchanged, result.Closed, records = snapshot.Records.Count, skipped,
        }, Json));
        return 0;
    }

    // CO-2d: a task ledger read as intent, each row a requirement (LedgerIntent); the source names the ledger's content.
    internal static Extensibility.Packs.ModuleSnapshot FromLedger(string ledger, string? source)
    {
        var json = File.ReadAllText(Path.GetFullPath(ledger));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..12].ToLowerInvariant();
        return LedgerIntent.Read(json, Path.GetFileName(ledger), source ?? "ledger:" + hash);
    }

    internal static Extensibility.Packs.ModuleSnapshot Read(string root, string? source, out IReadOnlyList<string> files, out IReadOnlyList<string> skipped, bool reviewed = true)
    {
        var listed = Git(root, "ls-files", "-z") is { } text ? text.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        files = IntentReader.IntentFiles(listed);
        var head = Git(root, "rev-parse", "HEAD")?.Trim();
        var snapshot = IntentReader.Read(root, files, source ?? (head is { Length: 40 or 64 } ? "git:" + head : "scan"), out skipped);
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
