using AgentMemory.Extensibility.Ampack;

namespace Ampack;

/// <summary>
/// Imports (features 15 and 16): <c>ampack import json &lt;file&gt; --mapping &lt;m.json&gt;</c>, <c>ampack import text &lt;file&gt; --source &lt;name&gt;</c>
/// and <c>ampack import folder &lt;dir&gt; [--git]</c>
/// write a signed knowledge pack of notes from another tool's memory export, with a derivation naming the source by its
/// digest and a card saying where it came from, and report every field and entry they left out.
/// </summary>
internal static partial class Program
{
    // ampack import json|text|folder <file or dir> --id <id> --version <v> --owner <name> --key <private.pem> --out <pack.ampack> [--mapping <m.json> | --source <name>]
    private static int Import(Options o)
    {
        var kind = o.Positional(0, "import kind (json, text)");
        var file = o.Positional(1, "the export file");
        var (id, version, owner) = (o.Required("id"), o.Value("version") ?? "1.0.0", o.Required("owner"));
        var key = Text(o.Required("key"), "The signing key");
        var output = o.Required("out");
        ImportResult result;
        try
        {
            if (kind == "folder")
            {
                var (files, commits) = MemoryFolder(file, o.Flag("git"));
                result = PackImports.FromFolder(files, commits, id, version, owner, key);
            }
            else
            {
            var source = AgentMemory.Packs.Format.PackReader.ReadFile(file, 64L << 20, "The export");
            result = kind switch
            {
                "json" => PackImports.FromJson(source, ImportMapping.Read(AgentMemory.Packs.Format.PackReader.ReadFile(o.Required("mapping"), 1 << 20, "The mapping")),
                    id, version, owner, key),
                "text" => PackImports.FromText(source, o.Value("source") ?? "an assistant", id, version, owner, key),
                _ => throw new UsageException($"Unknown import kind '{kind}': json, text, folder."),
            };
            }
        }
        catch (InvalidDataException e) { return Report([e.Message], ""); }
        File.WriteAllBytes(output, result.Archive);
        Console.WriteLine($"Imported {result.Records} note(s) into {id} {version}, signed; it is off on a host until its trial passes.\n  wrote   {output}");
        foreach (var (field, count) in result.Dropped) Console.WriteLine($"  dropped field '{Shown(field, 80)}' ({count} entr{(count == 1 ? "y" : "ies")})");
        foreach (var skip in result.Skipped) Console.WriteLine($"  skipped {Shown(skip, 200)}");
        foreach (var finding in result.Findings) Console.WriteLine($"  safety  {Shown(finding, 200)}");
        if (result.Findings.Count != 0) Console.WriteLine("  the safety scan found text to review before this pack is used (ampack scan shows it in full).");
        return 0;
    }

    // A memory folder's .md files, bounded (at most 10,000 files of 1 MiB each, no folder named .git), and with --git each file's
    // first commit from one 'git log' (renames are not followed: a renamed file dates from its rename).
    private static (Dictionary<string, byte[]> Files, Dictionary<string, DateTimeOffset>? Commits) MemoryFolder(string folder, bool git)
    {
        if (!Directory.Exists(folder)) throw new UsageException($"'{folder}' is not a folder.");
        var full = Path.GetFullPath(folder);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(full, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(full, path).Replace('\\', '/');
            if (relative.Split('/').Contains(".git")) continue;
            if (files.Count == 10_000) throw new UsageException("The folder holds more than 10,000 .md files.");
            files[relative] = AgentMemory.Packs.Format.PackReader.ReadFile(path, 1 << 20, $"'{relative}'");
        }
        if (!git) return (files, null);
        var start = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-C", full, "log", "--diff-filter=A", "--format=@%cI", "--name-only", "--relative", "--", "." }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new UsageException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new UsageException("--git needs the folder inside a git repository.");
        var commits = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var at = DateTimeOffset.MinValue;
        // Newest first, so a path's last mention is its first commit.
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0))
            if (line[0] == '@') at = DateTimeOffset.Parse(line[1..], System.Globalization.CultureInfo.InvariantCulture);
            else if (files.ContainsKey(line)) commits[line] = at;
        return (files, commits);
    }
}
