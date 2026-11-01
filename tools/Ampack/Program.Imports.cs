using System.IO.Compression;
using AgentMemory.Extensibility.Ampack;

namespace Ampack;

/// <summary>
/// Imports (features 15 and 16): <c>ampack import json &lt;file&gt; --mapping &lt;m.json&gt;</c>, <c>ampack import text &lt;file&gt; --source &lt;name&gt;</c>
/// <c>ampack import folder &lt;dir&gt; [--git]</c> and <c>ampack import mem &lt;bundle&gt; [--mem-trust &lt;public key hex&gt;...]</c> , <c>ampack import letta &lt;agent.af&gt;</c> and <c>ampack import mesh &lt;store folder&gt; [--mesh-key &lt;nodeId&gt;=&lt;key&gt;...]</c>
/// write a signed knowledge pack of notes from another tool's memory export, with a derivation naming the source by its
/// digest and a card saying where it came from, and report every field and entry they left out.
/// </summary>
internal static partial class Program
{
    // ampack import json|text|folder <file or dir> --id <id> --version <v> --owner <name> --key <private.pem> --out <pack.ampack> [--mapping <m.json> | --source <name>]
    private static int Import(Options o)
    {
        var kind = o.Positional(0, "import kind (json, text, folder, mem, letta, mesh)");
        var file = o.Positional(1, "the export file");
        var (id, version, owner) = (o.Required("id"), o.Value("version") ?? "1.0.0", o.Required("owner"));
        var key = Text(o.Required("key"), "The signing key");
        var output = o.Required("out");
        ImportResult result;
        try
        {
            if (kind == "mem")
            {
                var trusted = o.Values("mem-trust").Select(k => k.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
                if (trusted.Any(k => k.Length != 64 || !k.All(Uri.IsHexDigit))) throw new UsageException("--mem-trust is an Ed25519 public key as 64 hex digits.");
                result = PackImports.FromMem(MemBundle(file), Ed25519Verifies, trusted.Count == 0 ? null : trusted, id, version, owner, key);
            }
            else if (kind == "mesh")
            {
                // --mesh-key <author nodeId>=<Ed25519 public key, base64url>: the mesh pins author keys out of band.
                var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var given in o.Values("mesh-key"))
                {
                    var at = given.IndexOf('=');
                    var raw = at > 0 ? given[(at + 1)..].Replace('-', '+').Replace('_', '/') : "";
                    byte[]? publicKey = null;
                    try { publicKey = Convert.FromBase64String(raw + new string('=', (4 - raw.Length % 4) % 4)); } catch (FormatException) { }
                    if (at <= 0 || publicKey is not { Length: 32 }) throw new UsageException("--mesh-key is <author nodeId>=<Ed25519 public key in base64url>.");
                    keys[given[..at]] = publicKey;
                }
                result = PackImports.FromMeshMemory(MemBundle(file), id, version, owner, key, keys, Ed25519Verifies);
            }
            else if (kind == "folder")
            {
                var (files, commits, renames) = MemoryFolder(file, o.Flag("git"));
                var ontology = o.Value("ontology") is { } given ? AgentMemory.Packs.Format.PackReader.ReadFile(given, 1 << 20, "The ontology") : null;
                result = PackImports.FromFolder(files, commits, id, version, owner, key, renames, ontology);
                // A file git gave no date (never committed, or its name was not read): said, not silent (review 15-17, F4).
                if (commits is not null)
                    foreach (var undated in files.Keys.Where(k => k.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !commits.ContainsKey(k)).Order(StringComparer.Ordinal))
                        Console.WriteLine($"  no date '{Shown(undated, 200)}': git names no commit that added it");
            }
            else
            {
            var source = AgentMemory.Packs.Format.PackReader.ReadFile(file, 64L << 20, "The export");
            result = kind switch
            {
                "json" => PackImports.FromJson(source, ImportMapping.Read(AgentMemory.Packs.Format.PackReader.ReadFile(o.Required("mapping"), 1 << 20, "The mapping")),
                    id, version, owner, key),
                "text" => PackImports.FromText(source, o.Value("source") ?? "an assistant", id, version, owner, key),
                "letta" => PackImports.FromLetta(source, id, version, owner, key),
                _ => throw new UsageException($"Unknown import kind '{kind}': json, text, folder, mem, letta, mesh."),
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

    // Ed25519 (RFC 8032) through BouncyCastle: whether the signature verifies over the message under the public key.
    private static bool Ed25519Verifies(byte[] publicKey, byte[] signature, byte[] message) =>
        publicKey.Length == 32 && signature.Length == 64 && Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.ValidatePublicKeyFull(publicKey, 0)
        && Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);

    // A .mem bundle's files by their bundle-relative path: a folder (no links followed) or a zip archive, at most 10,000 files,
    // 64 MiB each and 256 MiB in all, every path relative with no '..' (the spec's "reject any path that escapes the bundle").
    private static Dictionary<string, byte[]> MemBundle(string path)
    {
        const long each = 64L << 20, all = 256L << 20;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        void Add(string relative, Func<long, byte[]> read, long size)
        {
            if (relative.Split('/').Any(part => part is "" or "." or ".."))
                throw new UsageException($"'{Shown(relative, 200)}' escapes the bundle.");
            if (files.Count == 10_000) throw new UsageException("The bundle holds more than 10,000 files.");
            if (size > each || (total += size) > all) throw new UsageException("The bundle is larger than an import reads (64 MiB a file, 256 MiB in all).");
            files[relative] = read(size);
        }
        if (Directory.Exists(path))
        {
            var top = new DirectoryInfo(path);
            if (top.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UsageException($"'{path}' is a link; give the bundle itself.");
            void Walk(DirectoryInfo dir, string prefix)
            {
                foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UsageException($"'{Shown(prefix + entry.Name, 200)}' is a link, which an import does not follow.");
                    if (entry is DirectoryInfo sub) Walk(sub, prefix + entry.Name + "/");
                    else Add(prefix + entry.Name, size => AgentMemory.Packs.Format.PackReader.ReadFile(entry.FullName, each, $"'{prefix + entry.Name}'"), ((FileInfo)entry).Length);
                }
            }
            Walk(top, "");
            return files;
        }
        using var zip = new ZipArchive(new MemoryStream(AgentMemory.Packs.Format.PackReader.ReadFile(path, all, "The bundle")), ZipArchiveMode.Read);
        // A bundle zipped with its folder at the top is read from inside that folder.
        var prefixOf = zip.Entries.Select(e => e.FullName).Where(n => n.Length != 0).Select(n => n.Split('/')[0]).Distinct().ToList() is [var only]
            && zip.Entries.All(e => e.FullName.StartsWith(only + "/", StringComparison.Ordinal)) ? only + "/" : "";
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            Add(entry.FullName[prefixOf.Length..], size =>
            {
                using var source = entry.Open();
                // Read at most one byte past the stated size, so an entry that inflates beyond it is caught, not held.
                var buffer = new MemoryStream();
                var chunk = new byte[81920];
                for (int n; buffer.Length <= size && (n = source.Read(chunk, 0, (int)Math.Min(chunk.Length, size + 1 - buffer.Length))) > 0;)
                    buffer.Write(chunk, 0, n);
                if (buffer.Length != size) throw new UsageException($"'{Shown(entry.FullName, 200)}' does not inflate to its stated size.");
                return buffer.ToArray();
            }, entry.Length);
        return files;
    }

    // A memory folder's .md files, bounded (at most 10,000 files of 1 MiB each, no folder named .git), walked without following
    // a link or reparse point (refused, as a pack folder refuses them: review 15-17, F6), and with --git each file's first
    // commit, by its author date, from one 'git log' read exactly (review 15-17, F4, F5, F10: names unquoted and in UTF-8,
    // renames not detected so a renamed file dates from its rename, each commit marked by a NUL no path holds).
    private static (Dictionary<string, byte[]> Files, Dictionary<string, DateTimeOffset>? Commits, List<FolderRename>? Renames) MemoryFolder(string folder, bool git)
    {
        var top = new DirectoryInfo(folder);
        if (!top.Exists) throw new UsageException($"'{folder}' is not a folder.");
        if (top.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UsageException($"'{folder}' is a link; give the folder itself.");
        var full = top.FullName;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Walk(DirectoryInfo dir, string prefix)
        {
            foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var relative = prefix + entry.Name;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UsageException($"'{Shown(relative, 200)}' is a link, which an import does not follow.");
                if (entry is DirectoryInfo sub) { if (entry.Name != ".git") Walk(sub, relative + "/"); continue; }
                if (!entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                if (files.Count == 10_000) throw new UsageException("The folder holds more than 10,000 .md files.");
                files[relative] = AgentMemory.Packs.Format.PackReader.ReadFile(entry.FullName, 1 << 20, $"'{relative}'");
            }
        }
        Walk(top, "");
        if (!git) return (files, null, null);
        // Each path's first commit, for every .md path git has seen added: a current file's start, and an old path's start
        // when the file was later renamed. Newest first, so a path's last mention is its first commit.
        var commits = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var at = DateTimeOffset.MinValue;
        foreach (var line in Git(full, folder, 64L << 20, false, "log", "--no-renames", "--diff-filter=A", "--format=%x00%aI", "--name-only", "--relative", "--", "."))
            if (line[0] == '\0') at = DateTimeOffset.Parse(line[1..], System.Globalization.CultureInfo.InvariantCulture);
            else if (line.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) commits[line] = at;
        // Renames (R3), each with its commit, the old path and the new, and the old file's bytes just before: at most 1,000.
        var renames = new List<FolderRename>();
        var commit = "";
        foreach (var line in Git(full, folder, 64L << 20, false, "log", "-M", "--diff-filter=R", "--format=%x00%H %aI", "--name-status", "--relative", "--", "."))
        {
            if (line[0] == '\0') { (commit, at) = (line[1..41], DateTimeOffset.Parse(line[42..], System.Globalization.CultureInfo.InvariantCulture)); continue; }
            if (line.Split('\t') is not [var status, var from, var to] || !status.StartsWith('R') || !from.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !to.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (renames.Count == 1000) throw new UsageException("--git: the folder's history holds more than 1,000 renames of .md files.");
            var before = string.Join('\n', Git(full, folder, 1 << 20, true, "show", $"{commit}^:./{from}"));
            renames.Add(new FolderRename(from, to, at, System.Text.Encoding.UTF8.GetBytes(before)));
        }
        return (files, commits, renames);
    }

    // git's output in the folder, line by line (empty lines kept when asked), at most `limit` characters; refused when git fails or is missing.
    private static List<string> Git(string full, string folder, long limit, bool keepEmpty, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-c", "core.quotePath=false", "-c", "i18n.logOutputEncoding=UTF-8", "-C", full }.Concat(args))
            start.ArgumentList.Add(a);
        System.Diagnostics.Process process;
        try { process = System.Diagnostics.Process.Start(start) ?? throw new UsageException("git could not be started."); }
        catch (System.ComponentModel.Win32Exception) { throw new UsageException("--git needs git on the PATH."); }
        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync();
            var buffer = new char[81920];
            var output = new System.Text.StringBuilder();
            for (int n; (n = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0;)
            {
                if (output.Length + n > limit) { process.Kill(); throw new UsageException($"--git: git's output in '{folder}' is larger than an import reads."); }
                output.Append(buffer, 0, n);
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new UsageException($"--git: git {args[0]} failed in '{folder}': {Shown(errors.Result.Trim(), 300)}");
            var lines = output.ToString().Split('\n').Select(l => l.TrimEnd('\r'));
            return keepEmpty ? [.. lines] : [.. lines.Where(l => l.Length > 0)];
        }
    }
}
