using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace Ampack;

/// <summary>
/// <c>ampack</c>: the memory pack tool. Every command that reads a pack verifies it first; nothing is written into a store
/// here. Exit codes: 0 done, 1 refused (the reasons are printed), 2 wrong usage.
/// </summary>
internal static class Program
{
    private const string Usage = """
        ampack <command> [options]

          init <folder> --id <reverse.dns.id> [--kind knowledge] [--version 0.1.0]
                                 start a pack folder (pack.json, an ontology, one record, .gitattributes)
          lint <folder|file>     check paths, the JSON profile, pack.json and, once built, the files list
          build <folder> [--out <file.ampack>] [--key <private.pem>] [--role publisher] [--stored]
                                 write META/files.json, optionally sign, and pack; prints the digest
          sign <folder> --key <private.pem> [--role publisher|curator|assurer]
          verify <folder|file> --policy <trust.json> [--ledger <ledger.json>] [--record] [--allow-downgrade]
          inspect <folder|file>  identity, digest, files and signatures, without trusting them
          keygen --out <folder> [--name <name>] [--prefix <id.prefix>]
                                 a P-256 key pair and a trust-policy entry for it
          lock <composite-folder> <member.ampack>...
                                 pin every member's id, version and digest in META/lock.json
        """;

    internal static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
        try
        {
            var options = Options.Parse(args.Skip(1));
            return args[0] switch
            {
                "init" => Init(options),
                "lint" => Lint(options),
                "build" => Build(options),
                "sign" => Sign(options),
                "verify" => Verify(options),
                "inspect" => Inspect(options),
                "keygen" => Keygen(options),
                "lock" => Lock(options),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}", 2),
            };
        }
        catch (UsageException e) { return Fail(e.Message, 2); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            return Fail(e.Message, 1);
        }
    }

    private static int Init(Options o)
    {
        var folder = o.Positional(0, "folder");
        var id = o.Required("id");
        if (!PackManifest.IsValidId(id)) throw new UsageException($"'{id}' is not a reverse-DNS id (for example io.example.notes).");
        var version = o.Value("version") ?? "0.1.0";
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new UsageException($"{folder} is not empty.");
        var manifest = new JsonObject
        {
            ["format"] = PackManifest.Format, ["id"] = id, ["version"] = version, ["kind"] = o.Value("kind") ?? "knowledge",
            ["tier"] = "open", ["licence"] = "CC-BY-4.0", ["contains"] = new JsonArray(), ["containableBy"] = new JsonArray(),
        };
        var ontology = JsonNode.Parse("""{"types":[{"name":"Note","properties":["title","text"],"key":"key"}],"relations":[]}""")!;
        Write(folder, PackManifest.FileName, Pretty(manifest));
        Write(folder, "ontology.json", Pretty(ontology));
        Write(folder, "records/note.jsonl", "{\"key\":\"note-1\",\"title\":\"First note\",\"text\":\"Replace me.\"}\n");
        Write(folder, ".gitattributes", PackBuilder.GitAttributes);
        Console.WriteLine($"Started {id} {version} in {folder}. Next: ampack build {folder}");
        return 0;
    }

    private static int Lint(Options o)
    {
        var content = PackReader.Read(o.Positional(0, "folder or file"));
        var problems = PackPaths.Problems(content.Files.Keys).ToList();
        foreach (var (path, bytes) in content.Files.Where(f => FilesList.IsListed(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var problem = path.EndsWith(".jsonl", StringComparison.Ordinal) ? PackJson.CheckLines(bytes)
                : path.EndsWith(".json", StringComparison.Ordinal) ? PackJson.Check(bytes) : null;
            if (problem is not null) problems.Add($"'{path}' {problem}.");
        }
        if (content.Files.TryGetValue(PackManifest.FileName, out var manifest))
        {
            if (PackManifest.Parse(manifest).Problem is { } problem) problems.Add(problem);
        }
        else problems.Add($"There is no {PackManifest.FileName}.");
        if (problems.Count == 0 && content.Files.ContainsKey(PackPaths.FilesList)) problems.AddRange(PackVerifier.CheckIntegrity(content).Problems);
        return Report(problems, "Lint passed.");
    }

    private static int Build(Options o)
    {
        var folder = o.Positional(0, "folder");
        if (!Directory.Exists(folder)) throw new UsageException($"No pack folder at {folder}.");
        var (files, dropped) = PackBuilder.Build(PackReader.ReadFolder(folder).Files);
        var integrity = PackVerifier.CheckIntegrity(new PackContent(files, folder));
        if (integrity.Problems.Count > 0) return Report(integrity.Problems, "");
        if (dropped) Console.WriteLine("The content changed, so the old signatures were removed.");
        if (o.Value("key") is { } keyPath) PackBuilder.Sign(files, File.ReadAllText(keyPath), o.Value("role") ?? "publisher", SigningTime());
        WriteMeta(folder, files);
        var digest = FilesList.DigestOf(files[PackPaths.FilesList]);
        var output = o.Value("out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder))!, $"{integrity.List!.Id}-{integrity.List.Version}{PackReader.Extension}");
        File.WriteAllBytes(output, PackBuilder.Pack(files, compress: !o.Flag("stored")));
        Console.WriteLine($"{integrity.List!.Id} {integrity.List.Version}\n  digest  {digest}\n  files   {integrity.List.Files.Count}\n  signed  {files.Keys.Count(PackPaths.IsSignature)}\n  wrote   {output}");
        return 0;
    }

    private static int Sign(Options o)
    {
        var folder = o.Positional(0, "folder");
        var files = PackReader.ReadFolder(folder).Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var integrity = PackVerifier.CheckIntegrity(new PackContent(files, folder));
        if (integrity.Problems.Count > 0) return Report(integrity.Problems, "");
        var path = PackBuilder.Sign(files, File.ReadAllText(o.Required("key")), o.Value("role") ?? "publisher", SigningTime());
        WriteMeta(folder, files);
        Console.WriteLine($"Signed {integrity.List!.Id} {integrity.List.Version}: {path}");
        return 0;
    }

    private static int Verify(Options o)
    {
        var target = o.Positional(0, "folder or file");
        var policy = TrustPolicy.Parse(File.ReadAllBytes(o.Required("policy")));
        var ledgerPath = o.Value("ledger");
        var ledger = ledgerPath is null ? null : HostLedger.Load(ledgerPath);
        var result = PackVerifier.Verify(PackReader.Read(target), policy, DateTimeOffset.UtcNow, ledger, o.Flag("allow-downgrade"));
        if (!result.IsVerified) return Report(result.Problems, "");
        var pack = result.Pack!;
        Console.WriteLine($"Verified {pack.Id} {pack.Version}\n  digest  {pack.Digest}\n  kind    {pack.Manifest.Kind}, tier {pack.Manifest.Tier}");
        foreach (var s in pack.Signatures) Console.WriteLine($"  signed  {s.Role} by {Dsse.Short(s.KeyId)} at {s.SignedAt:u}");
        if (o.Flag("record") && ledger is not null && ledgerPath is not null)
        {
            ledger.Record(pack.Id, pack.Version, pack.Digest);
            ledger.Save(ledgerPath);
            Console.WriteLine($"  ledger  recorded in {ledgerPath}");
        }
        return 0;
    }

    private static int Inspect(Options o)
    {
        var content = PackReader.Read(o.Positional(0, "folder or file"));
        Console.WriteLine($"Source  {content.Source}");
        if (content.Files.TryGetValue(PackPaths.FilesList, out var listBytes) && FilesList.Parse(listBytes) is ({ } list, _))
        {
            Console.WriteLine($"Pack    {list.Id} {list.Version}\nDigest  {FilesList.DigestOf(listBytes)}  (not verified)");
            foreach (var f in list.Files) Console.WriteLine($"  {f.Length,10:N0}  {f.Path}");
        }
        else Console.WriteLine("Not built: no valid META/files.json.");
        foreach (var path in content.Files.Keys.Where(PackPaths.IsSignature).Order(StringComparer.Ordinal)) Console.WriteLine($"  envelope  {path}");
        return 0;
    }

    private static int Keygen(Options o)
    {
        var folder = o.Required("out");
        var name = o.Value("name") ?? "publisher";
        var key = PackKey.Create();
        Directory.CreateDirectory(folder);
        var privatePath = Path.Combine(folder, name + ".key.pem");
        if (File.Exists(privatePath)) throw new UsageException($"{privatePath} exists; refusing to overwrite a key.");
        File.WriteAllText(privatePath, key.PrivateKeyPem);
        File.WriteAllText(Path.Combine(folder, name + ".pub.pem"), key.PublicKeyPem);
        var entry = new JsonObject
        {
            ["keyId"] = key.KeyId, ["publicKey"] = key.PublicKeyPem,
            ["idPrefixes"] = new JsonArray((JsonNode)(o.Value("prefix") ?? "io.example")), ["roles"] = new JsonArray((JsonNode)"publisher"),
        };
        Console.WriteLine($"Key id  {key.KeyId}\nPrivate {privatePath}  (keep it secret; never commit it)\nTrust-policy entry:\n{Pretty(entry)}");
        return 0;
    }

    private static int Lock(Options o)
    {
        var folder = o.Positional(0, "composite folder");
        var members = new JsonArray();
        foreach (var memberPath in o.PositionalFrom(1).Order(StringComparer.Ordinal))
        {
            var content = PackReader.Read(memberPath);
            var integrity = PackVerifier.CheckIntegrity(content);
            if (integrity.Problems.Count > 0) return Report(integrity.Problems.Select(p => $"{memberPath}: {p}"), "");
            members.Add((JsonNode)new JsonObject
            {
                ["id"] = integrity.List!.Id, ["version"] = integrity.List.Version, ["digest"] = FilesList.DigestOf(integrity.ListBytes!),
            });
        }
        var ids = members.Select(m => m!["id"]!.GetValue<string>()).ToList();
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count()) return Report(["Two members share an id: a lock pins exactly one version of each pack."], "");
        var sorted = new JsonArray([.. members.OrderBy(m => m!["id"]!.GetValue<string>(), StringComparer.Ordinal).Select(m => m!.DeepClone())]);
        Write(folder, PackPaths.LockFile, Encoding.UTF8.GetString(Jcs.Canonical(new JsonObject { ["format"] = "agentmemory.pack.lock/1.1", ["members"] = sorted })));
        Console.WriteLine($"Locked {sorted.Count} members in {Path.Combine(folder, PackPaths.LockFile)}. Rebuild and sign the composite.");
        return 0;
    }

    /// <summary>SOURCE_DATE_EPOCH when set (reproducible builds), otherwise now, to the second.</summary>
    private static DateTimeOffset SigningTime() =>
        Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH") is { } epoch && long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>Writes META/files.json and the signatures back into the folder, removing envelopes that no longer apply.</summary>
    private static void WriteMeta(string folder, IReadOnlyDictionary<string, byte[]> files)
    {
        var signatures = Path.Combine(folder, "META", "signatures");
        if (Directory.Exists(signatures))
            foreach (var stale in Directory.EnumerateFiles(signatures))
                if (!files.ContainsKey("META/signatures/" + Path.GetFileName(stale))) File.Delete(stale);
        foreach (var (path, bytes) in files.Where(f => f.Key.StartsWith("META/", StringComparison.Ordinal) && f.Key != PackPaths.LockFile))
            Write(folder, path, bytes);
    }

    private static void Write(string folder, string path, string text) => Write(folder, path, Encoding.UTF8.GetBytes(text));

    private static void Write(string folder, string path, byte[] bytes)
    {
        var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static string Pretty(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true })) node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static int Report(IEnumerable<string> problems, string success)
    {
        var list = problems.ToList();
        if (list.Count == 0) { Console.WriteLine(success); return 0; }
        foreach (var p in list) Console.Error.WriteLine("refused: " + p);
        return 1;
    }

    private static int Fail(string message, int code)
    {
        Console.Error.WriteLine(message);
        return code;
    }
}

/// <summary>A command line that does not say what the command needs.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Positional arguments, <c>--name value</c> options and <c>--flag</c> switches.</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "stored", "record", "allow-downgrade" };
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    internal static Options Parse(IEnumerable<string> args)
    {
        var options = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal)) { options._positional.Add(list[i]); continue; }
            var name = list[i][2..];
            if (Flags.Contains(name)) { options._flags.Add(name); continue; }
            if (i + 1 >= list.Count) throw new UsageException($"--{name} needs a value.");
            options._values[name] = list[++i];
        }
        return options;
    }

    internal string Positional(int index, string what) =>
        index < _positional.Count ? _positional[index] : throw new UsageException($"Missing the {what}.");

    internal IEnumerable<string> PositionalFrom(int index) => _positional.Skip(index);
    internal string? Value(string name) => _values.GetValueOrDefault(name);
    internal string Required(string name) => Value(name) ?? throw new UsageException($"Missing --{name}.");
    internal bool Flag(string name) => _flags.Contains(name);
}
