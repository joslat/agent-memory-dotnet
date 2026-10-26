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

          init <folder> --id <reverse.dns.id> [--kind knowledge|definition] [--version 0.1.0]
                                 start a pack folder (pack.json, an ontology, a view, .gitattributes, and for a
                                 knowledge pack one record)
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
          migrate <bundle-folder|manifest.json> --out <folder>
                                 turn a module bundle (manifest.json, seed files, icon) into a v1.2 pack folder:
                                 a knowledge pack when it seeds records, a definition pack when it only declares,
                                 a composite pack for a composite manifest (then: ampack lock, ampack build)
          set <out.ampack> <member.ampack>... --id <id> --version <v> --key <private.pem>
                                 one archive carrying complete packs side by side, signed as a whole
          test <pack.ampack>... --policy <trust.json>
                                 load the packs as a host does (every record checked), hold them in memory, and run
                                 each pack's own tests (evals/tests.json): views, searches and walks, offline;
                                 give a snapshot's definition pack beside it
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
                "migrate" => Migrate(options),
                "set" => Set(options),
                "test" => Test(options),
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
        var kind = o.Value("kind") ?? "knowledge";
        if (kind is not ("knowledge" or "definition"))
            throw new UsageException($"init makes a knowledge or a definition pack; a {kind} pack is made with ampack lock, set or codemap pack.");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new UsageException($"{folder} is not empty.");
        // The label prefix every type and relation carries: the id's last segment, letters only, capitalised.
        var last = new string([.. id[(id.LastIndexOf('.') + 1)..].Where(char.IsAsciiLetter)]);
        var prefix = last.Length == 0 ? "Pack" : char.ToUpperInvariant(last[0]) + last[1..];
        var manifest = new JsonObject
        {
            ["format"] = PackManifest.Format, ["id"] = id, ["version"] = version, ["kind"] = kind, ["sdk"] = "[0.1,0.2)",
            ["display"] = new JsonObject { ["title"] = prefix, ["description"] = "Describe what this pack knows." },
            ["tier"] = "open", ["licence"] = "CC-BY-4.0", ["protection"] = "open", ["code"] = "E0",
        };
        static JsonObject Text(int maxLength) => new() { ["type"] = "string", ["required"] = true, ["maxLength"] = maxLength };
        var ontology = new JsonObject
        {
            ["labelPrefix"] = prefix,
            ["types"] = new JsonArray((JsonNode)new JsonObject
            {
                ["name"] = prefix + "Note", ["scope"] = "shared", ["render"] = "{title}: {text}",
                ["properties"] = new JsonObject { ["title"] = Text(200), ["text"] = Text(2000) },
            }),
        };
        var views = new JsonArray((JsonNode)new JsonObject
        {
            ["id"] = "notes", ["type"] = prefix + "Note", ["limit"] = 3,
            ["search"] = new JsonObject { ["properties"] = new JsonArray((JsonNode)"title", (JsonNode)"text") },
        });
        var record = new JsonObject
        {
            ["type"] = prefix + "Note", ["key"] = "note-1",
            ["properties"] = new JsonObject { ["title"] = "First note", ["text"] = "Replace me." },
        };
        Write(folder, PackManifest.FileName, Pretty(manifest));
        Write(folder, PackLayout.Ontology, Pretty(ontology));
        Write(folder, PackLayout.Views, Pretty(views));
        if (kind == "knowledge") Write(folder, "records/note.jsonl", record.ToJsonString() + "\n");
        Write(folder, ".gitattributes", PackBuilder.GitAttributes);
        Console.WriteLine($"Started {id} {version} in {folder}. Next: ampack build {folder}");
        return 0;
    }

    private static int Migrate(Options o)
    {
        var source = o.Positional(0, "bundle folder or manifest.json");
        var output = o.Required("out");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new UsageException($"{output} is not empty.");
        IReadOnlyDictionary<string, byte[]> bundle;
        if (File.Exists(source))
            bundle = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PackLayout.ModuleManifestFile] = File.ReadAllBytes(source) };
        else
        {
            var root = new DirectoryInfo(source);
            if (!root.Exists) throw new UsageException($"No bundle at {source}.");
            bundle = root.EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.Name.StartsWith("agentmemory.module.", StringComparison.Ordinal))
                .ToDictionary(f => Path.GetRelativePath(root.FullName, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), f => File.ReadAllBytes(f.FullName), StringComparer.Ordinal);
        }
        var files = PackLayout.FromModule(bundle);
        foreach (var (path, bytes) in files) Write(output, path, bytes);
        Write(output, ".gitattributes", PackBuilder.GitAttributes);
        var pack = JsonNode.Parse(files[PackManifest.FileName])!;
        Console.WriteLine($"Migrated {pack["id"]} {pack["version"]} as a {pack["kind"]} pack: {files.Count} files in {output}. Next: ampack build {output} --key <key>");
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
        var content = PackReader.Read(target);
        var result = PackVerifier.Verify(content, policy, DateTimeOffset.UtcNow, ledger, o.Flag("allow-downgrade"));
        if (!result.IsVerified) return Report(result.Problems, "");
        var pack = result.Pack!;
        if (pack.Manifest.Kind == "set")
        {
            // A set verifies as a whole and member by member: each keeps its own list and signatures.
            var (_, members) = PackVerifier.VerifySet(content, policy, DateTimeOffset.UtcNow, ledger, o.Flag("allow-downgrade"));
            var refused = members.Where(m => !m.Value.IsVerified).SelectMany(m => m.Value.Problems.Select(p => $"packs/{m.Key}/: {p}")).ToList();
            if (refused.Count > 0) return Report(refused, "");
            Console.WriteLine($"Verified set {pack.Id} {pack.Version}\n  digest  {pack.Digest}");
            foreach (var (id, member) in members.OrderBy(m => m.Key, StringComparer.Ordinal))
                Console.WriteLine($"  member  {id} {member.Pack!.Version} ({member.Pack.Manifest.Kind}) {member.Pack.Digest}");
            if (o.Flag("record") && ledger is not null && ledgerPath is not null)
            {
                ledger.Record(pack.Id, pack.Version, pack.Digest);
                foreach (var member in members.Values) ledger.Record(member.Pack!.Id, member.Pack.Version, member.Pack.Digest);
                ledger.Save(ledgerPath);
                Console.WriteLine($"  ledger  recorded the set and {members.Count} members in {ledgerPath}");
            }
            return 0;
        }
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

    private static int Set(Options o)
    {
        var output = o.Positional(0, "output .ampack");
        var id = o.Required("id");
        if (!PackManifest.IsValidId(id)) throw new UsageException($"'{id}' is not a reverse-DNS id.");
        var version = o.Value("version") ?? "0.1.0";
        var members = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal);
        foreach (var path in o.PositionalFrom(1))
        {
            var content = PackReader.Read(path);
            var integrity = PackVerifier.CheckIntegrity(content);
            if (integrity.Problems.Count > 0) return Report(integrity.Problems.Select(p => $"{path}: {p}"), "");
            if (!content.Files.Keys.Any(PackPaths.IsSignature)) return Report([$"{path}: a set carries signed members only."], "");
            if (!members.TryAdd(integrity.List!.Id, content.Files)) return Report([$"{integrity.List.Id} is given twice."], "");
        }
        if (members.Count == 0) throw new UsageException("A set carries at least one member.");
        var (files, _) = PackBuilder.Build(PackSets.Source(id, version, members));
        PackBuilder.Sign(files, File.ReadAllText(o.Required("key")), o.Value("role") ?? "publisher", SigningTime());
        File.WriteAllBytes(output, PackBuilder.Pack(files, compress: !o.Flag("stored")));
        Console.WriteLine($"Set {id} {version}: {members.Count} members\n  digest  {FilesList.DigestOf(files[PackPaths.FilesList])}\n  wrote   {output}");
        return 0;
    }

    // PACK-toolset: a pack's own tests, on the packs loaded as a host loads them and held in memory.
    private static int Test(Options o)
    {
        var trust = AgentMemory.Extensibility.Ampack.PackTrust.Load(o.Required("policy"));
        var paths = o.PositionalFrom(0).ToList();
        if (paths.Count == 0) throw new UsageException("test needs at least one pack.");
        var loaded = new List<AgentMemory.Extensibility.Ampack.LoadedPack>();
        try
        {
            foreach (var path in paths)
            {
                var bytes = File.ReadAllBytes(path);
                switch (AgentMemory.Extensibility.Ampack.PackLoader.Peek(bytes).Kind)
                {
                    case "set":
                        var set = AgentMemory.Extensibility.Ampack.PackFamilies.LoadSet(bytes, trust);
                        loaded.AddRange(set.Families.SelectMany(f => f.Members).Concat(set.Packs));
                        break;
                    case "composite":
                        throw new UsageException($"{path} is a composite: test the set that carries it with its members.");
                    default:
                        loaded.Add(AgentMemory.Extensibility.Ampack.PackLoader.Load(bytes, trust));
                        break;
                }
            }
            var definitions = loaded.Where(p => p.Kind == "definition").ToDictionary(p => p.Id, p => p.Manifest, StringComparer.Ordinal);
            var snapshots = new List<AgentMemory.Extensibility.Packs.ModuleSnapshot>();
            foreach (var shot in loaded.Where(p => p.Kind == "snapshot"))
            {
                var of = shot.Manifest.Modules.Keys.Single();
                if (!definitions.TryGetValue(of, out var definition)) return Report([$"{shot.Id} is a snapshot of {of}: give that definition pack beside it."], "");
                var dependencies = definition.Modules.Keys.Where(definitions.ContainsKey).Select(d => definitions[d]).ToList();
                snapshots.Add(AgentMemory.Extensibility.Ampack.PackLoader.ToSnapshot(shot, definition, dependencies));
            }
            var memory = AgentMemory.Extensibility.Ampack.PackMemory.From(loaded.Select(p => p.Knowledge).OfType<AgentMemory.Extensibility.Packs.KnowledgePack>(), snapshots);
            Console.WriteLine($"Loaded {loaded.Count} pack(s), {memory.Count:N0} records in memory.");
            var failed = 0; var ran = 0;
            foreach (var pack in loaded)
            {
                var results = AgentMemory.Extensibility.Ampack.PackTests.RunAsync(pack, memory).GetAwaiter().GetResult();
                if (results.Count == 0) { Console.WriteLine($"  {pack.Id}: no tests (evals/tests.json)"); continue; }
                foreach (var result in results)
                {
                    ran++;
                    if (!result.Passed) failed++;
                    Console.WriteLine($"  {(result.Passed ? "PASS" : "FAIL")}  {pack.Id}: {result.Name}: {result.Detail}");
                }
            }
            Console.WriteLine(ran == 0 ? "No tests to run." : $"{ran - failed} of {ran} passed.");
            return failed == 0 ? 0 : 1;
        }
        catch (AgentMemory.Extensibility.Ampack.PackLoadException e) { return Report(e.Problems, ""); }
        catch (AgentMemory.Extensibility.Packs.KnowledgePackException e) { return Report(e.Problems, ""); }
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
