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
internal static partial class Program
{
    private const string Usage = """
        ampack <command> [options]

          init <folder> --id <reverse.dns.id> [--kind knowledge|definition] [--version 0.1.0]
                                 start a pack folder (pack.json, an ontology, a view, .gitattributes, and for a
                                 knowledge pack one record)
          lint <folder|file> [--policy <trust.json>]
                                 check paths, the JSON profile, pack.json and, once built, the files list; with
                                 --policy, load a .ampack as a host does and give advice on what a host will do with
                                 it later (no trial, no upgrade rule, links that need consent, validity ending soon)
          build <folder> [--out <file.ampack>] [--key <private.pem>] [--role publisher] [--stored]
                                 write META/files.json, optionally sign, and pack; prints the digest
          sign <folder> --key <private.pem> [--role publisher|curator|assurer]
          verify <folder|file> --policy <trust.json> [--revocations <file>] [--ledger <ledger.json>] [--record] [--allow-downgrade]
                 [--explain]       with --explain, every step in order: what it checked, whether it held, and why
          inspect <folder|file>  identity, digest, files and signatures, without trusting them
          diff <a> <b> [--policy <trust.json>] [--json]
                                 what changed between two packs: identity, every declared value (ontology, views,
                                 walk, trial, ...), every record by type and key, every file; --policy verifies both first
          keygen --out <folder> [--name <name>] [--prefix <id.prefix>] [--role publisher|curator|assurer|revoker]
                 [--protect dpapi] [--policy <trust.json>]
                                 a P-256 key pair and a trust-policy entry for it; --protect dpapi keeps the
                                 private key encrypted for this Windows user (use it as --key dpapi:<file>);
                                 --policy adds the key to a trust policy file, creating it when missing
          Every --key also takes env:<VARIABLE> or dpapi:<file>; or sign with --signer "<command>" --public <pub.pem>
          for an OS keychain, KMS or HSM (the command reads the bytes to sign, prints the signature in base64).
          sign-bytes --key <private.pem> [--p1363]
                                 the reference --signer: read bytes on standard input, print their signature in base64
                                 (DER, or r||s with --p1363, as KMS tools print it)
          revoke <out.json> --key <revoker.pem> --expires <RFC 3339> [--digests <sha256:…,…>] [--keys <keyId[@compromisedAt],…>]
                                 a signed revocation document; hosts apply it with --revocations, and nothing
                                 verifies after it expires until a fresh one is applied
          ledger --retire-revocations (--ledger <ledger.json> | --store <folder>)
                                 for a host that stops using revocation lists: forget the newest list's freshness
                                 (verifying is stale after it expires); every revocation stays
          lock <composite-folder> <member.ampack>...
                                 pin every member's id, version and digest in META/lock.json
          migrate <bundle-folder|manifest.json> --out <folder>
                                 turn a module bundle (manifest.json, seed files, icon) into a v1.2 pack folder:
                                 a knowledge pack when it seeds records, a definition pack when it only declares,
                                 a composite pack for a composite manifest (then: ampack lock, ampack build)
          set <out.ampack> <member.ampack>... --id <id> --version <v> --key <private.pem>
                                 one archive carrying complete packs side by side, signed as a whole
          install <pack.ampack> --store <folder> --policy <trust.json> [--revocations <file>] [--allow-downgrade]
                                 verify a pack or pack set in full and install it in a host's pack store; the
                                 store's ledger refuses an older version or other content under a known version
          uninstall <id> --store <folder>
                                 remove an installed pack (the ledger keeps it)
          list --store <folder>  the installed packs
          serve [<pack.ampack>...] [--store <folder>] --policy <trust.json> [--revocations <file>]
                [--http <http://localhost:5199/> [--allow-remote]]
                                 mount the packs in memory and serve them over MCP on standard input and output:
                                 pack_list, pack_ask (a view: search text or a key), pack_walk (from files and symbols);
                                 with --http, the same over REST: GET /v1/meta, GET /v1/packs,
                                 POST /v1/packs/{id}/views/{view}/ask, POST /v1/walk (this machine only, unless
                                 --allow-remote)
          evidence --activations <folder> --host-key <host.pub.pem> --pack <id> [--version <v>] --out <run-folder>
                                 a host's trial decision for a pack (the latest, or of that version) as an AEF run:
                                 the pack as its subject, the trial's tasks with and without it, the gate's decision,
                                 and the signed record itself; an assurer seals and signs it (agenteval aef seal
                                 --sealed-by ingest --key ...)
          scan <folder|file> [--policy <trust.json>] [--aef <run-folder>]
                                 look in every record and declaration for personal data, secrets and prompt-injection
                                 payloads; exit 1 when anything is found; with --policy, scan only a pack that
                                 verifies; with --aef, write the scan as an AEF run for an assurer to seal and sign
          countersign <pack.ampack> --key <file|env:VAR|dpapi:FILE> --role <curator|assurer|publisher> --out <file>
                                 sign a built pack again in another role, its content and digest unchanged
          receipt sign <pack.ampack> --location <url> --key <...> --out <file> [--audience public|<WebID,…>]
                 [--access wac|acp|public] [--denied anonymous,<WebID>,…] [--allowed <WebID>,…]
                                 a publication receipt for a federated pack placed at a URL: that URL, those exact
                                 bytes, an audience no wider than the pack's (default: the pack's), and what was probed
          receipt verify <pack.ampack> <receipt> --location <url> --policy <trust.json> [--revocations <file>]
                                 verify the pack and its receipt as a host fetching it from that URL does
          trust webid <WebID> [--key <keyId> --policy <trust.json> --prefix <id.prefix> [--role publisher] [--host-webid <WebID>]]
                                 list the P-256 keys a WebID's profile asserts; with --key, bind that key to the WebID
                                 in the trust policy (never trust on first use: name the key you checked)
          solid bind-key --webid <WebID> --public <pub.pem>
                                 assert a signing key in your own WebID profile (sec:assertionMethod, a P-256 JWK)
          solid publish <pack.ampack> --webid <WebID> --key <...> [--audience <WebID,…>] [--probe-as <WebID>]
                                 put a federated pack in your Pod for its audience (WAC), probe that anonymous is refused
                                 (and that --probe-as reads the exact bytes), sign the receipt, update the catalogs and
                                 the type index
          solid discover --from <publisher WebID> [--webid <WebID>]
                                 what a publisher offers you (or anyone): from its profile's type index to its catalogs
          solid fetch --from <publisher WebID> --webid <WebID> --policy <trust.json> --store <folder> [--id <pack id>]
                [--no-recheck] [--allow-downgrade]
                                 fetch, re-check the publisher's key in its profile, verify pack and receipt as this host,
                                 install, and log where it came from (<store>/federation.jsonl)
          Solid commands log in with client credentials from AMPACK_SOLID_CLIENT_ID and AMPACK_SOLID_CLIENT_SECRET (a probe
          identity's from AMPACK_PROBE_CLIENT_ID and AMPACK_PROBE_CLIENT_SECRET); --allow-loopback-http for a local server.
          test [<pack.ampack>...] [--store <folder>] --policy <trust.json> [--revocations <file>]
                                 load the packs as a host does (every record checked), hold them in memory, and run
                                 each pack's own tests (evals/tests.json): views, searches and walks, offline;
                                 give a snapshot's definition pack beside it
        """;

    internal static int Main(string[] args)
    {
        // Messages name packs, digests and paths with non-ASCII characters (…, ≥); write them as UTF-8 on every console.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
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
                "diff" => DiffPacks(options),
                "keygen" => Keygen(options),
                "revoke" => Revoke(options),
                "ledger" => Ledger(options),
                "sign-bytes" => SignBytes(options),
                "lock" => Lock(options),
                "migrate" => Migrate(options),
                "set" => Set(options),
                "test" => Test(options),
                "evidence" => Evidence(options),
                "scan" => ScanPack(options),
                "countersign" => Countersign(options),
                "serve" => ServePacks(options),
                "install" => Install(options),
                "uninstall" => Uninstall(options),
                "list" => ListInstalled(options),
                "receipt" => Receipt(options),
                "solid" => Solid(options),
                "trust" => TrustCommand(options),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}", 2),
            };
        }
        catch (UsageException e) { return Fail(e.Message, 2); }
        // A ledger another run holds is an operational failure, not a refusal (seventh review).
        catch (LedgerBusyException e) { return Fail(e.Message, 3); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or System.Security.Cryptography.CryptographicException
            or HttpRequestException or TaskCanceledException)
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
        if (problems.Count > 0 || o.Value("policy") is null) return Report(problems, "Lint passed.");
        // Advice needs the pack as a host loads it: a built .ampack, against the host's trust.
        var target = o.Positional(0, "folder or file");
        if (!File.Exists(target)) throw new UsageException("Advice (--policy) reads a built .ampack; build the folder first.");
        var inspection = AgentMemory.Extensibility.Ampack.PackInspection.Inspect(File.ReadAllBytes(target), Trust(o));
        if (!inspection.Loaded) return Report(inspection.Problems, "");
        var advice = AgentMemory.Extensibility.Ampack.PackAdvice.Of(inspection, DateTimeOffset.UtcNow);
        Console.WriteLine(advice.Count == 0 ? "Lint passed; no advice." : $"Lint passed; {advice.Count} piece(s) of advice:");
        foreach (var item in advice) Console.WriteLine($"  {item.Rule,-20} {item.Pack}: {item.Text}");
        return 0;
    }

    private static int Build(Options o)
    {
        var folder = o.Positional(0, "folder");
        if (!Directory.Exists(folder)) throw new UsageException($"No pack folder at {folder}.");
        var (files, dropped) = PackBuilder.Build(PackReader.ReadFolder(folder).Files);
        var integrity = PackVerifier.CheckIntegrity(new PackContent(files, folder));
        if (integrity.Problems.Count > 0) return Report(integrity.Problems, "");
        if (dropped) Console.WriteLine("The content changed, so the old signatures were removed.");
        if (o.Value("key") is not null || o.Value("signer") is not null)
        {
            using var signer = Signer(o);
            PackBuilder.Sign(files, signer, o.Value("role") ?? "publisher", SigningTime());
        }
        WriteMeta(folder, files);
        var digest = FilesList.DigestOf(files[PackPaths.FilesList]);
        var output = o.Value("out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder))!, $"{integrity.List!.Id}-{integrity.List.Version}{PackReader.Extension}");
        File.WriteAllBytes(output, PackBuilder.Pack(files, compress: !o.Flag("stored")));
        Console.WriteLine($"{integrity.List!.Id} {integrity.List.Version}\n  digest  {digest}\n  files   {integrity.List.Files.Count}\n  signed  {files.Keys.Count(PackPaths.IsSignature)}\n  wrote   {output}");
        return 0;
    }

    // PACK-evidence-aef: a host's trial decision as an AEF run.
    private static int Evidence(Options o)
    {
        var records = new AgentMemory.Extensibility.Modules.PackActivationFolder(o.Required("activations"), File.ReadAllText(o.Required("host-key")));
        var id = o.Required("pack");
        var version = o.Value("version");
        var decision = records.History(id).Where(d => version is null || d.Version == version).MaxBy(d => d.DecidedAt);
        if (decision is null) return Report([$"No decision signed by this host for {id}{(version is null ? "" : " " + version)} in {o.Required("activations")}."], "");
        var file = records.FileOf(decision);
        var runId = AgentMemory.Extensibility.Ampack.PackTrialEvidence.WriteRun(decision, o.Required("out"), file is null ? null : File.ReadAllBytes(file),
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
        Console.WriteLine($"{id} {decision.Version}: {(decision.Active ? "on" : "off")}, {decision.Reason}\n  run     {runId}\n  wrote   {o.Required("out")}");
        return 0;
    }

    // PACK-scan: personal data, secrets and injection payloads, before a pack travels.
    private static int ScanPack(Options o)
    {
        var target = o.Positional(0, "folder or file");
        var content = PackReader.Read(target);
        if (o.Value("policy") is not null && PackVerifier.Verify(content, Policy(o), DateTimeOffset.UtcNow) is { IsVerified: false } refused)
            return Report(refused.Problems, "");
        var report = AgentMemory.Extensibility.Ampack.PackScan.Scan(target);
        foreach (var finding in report.Findings)
            Console.WriteLine($"  {finding.Severity,-6} {finding.Rule,-34} {finding.File}{(finding.Line > 0 ? ":" + finding.Line : "")} {finding.Path}  {finding.Sample}");
        Console.WriteLine(report.Clean ? $"Clean: {report.Strings} strings in {report.Files} files." : $"{report.Findings.Count} finding(s) in {report.Strings} strings in {report.Files} files.");
        if (o.Value("aef") is { } folder)
        {
            if (!content.Files.TryGetValue(PackPaths.FilesList, out var listBytes) || FilesList.Parse(listBytes).List is not { } list)
                return Report(["An AEF run names the pack by its digest: build the pack first."], "");
            var runId = AgentMemory.Extensibility.Ampack.PackScan.WriteRun(report, list.Id, list.Version, FilesList.DigestOf(listBytes), DateTimeOffset.UtcNow, folder,
                typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
            Console.WriteLine($"  run     {runId}\n  wrote   {folder}");
        }
        return report.Clean ? 0 : 1;
    }

    private static int Countersign(Options o)
    {
        var target = o.Positional(0, "pack file");
        using var signer = Signer(o);
        var role = o.Required("role");
        var content = PackReader.ReadArchive(File.ReadAllBytes(target), target);
        if (PackVerifier.CheckIntegrity(content).Problems is { Count: > 0 } problems) return Report(problems, "");
        var files = content.Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        PackBuilder.Sign(files, signer, role, SigningTime());
        File.WriteAllBytes(o.Required("out"), PackBuilder.Pack(files));
        Console.WriteLine($"Countersigned as {role}: {o.Required("out")}");
        return 0;
    }

    private static int Sign(Options o)
    {
        var folder = o.Positional(0, "folder");
        var files = PackReader.ReadFolder(folder).Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var integrity = PackVerifier.CheckIntegrity(new PackContent(files, folder));
        if (integrity.Problems.Count > 0) return Report(integrity.Problems, "");
        using var signer = Signer(o);
        var path = PackBuilder.Sign(files, signer, o.Value("role") ?? "publisher", SigningTime());
        WriteMeta(folder, files);
        Console.WriteLine($"Signed {integrity.List!.Id} {integrity.List.Version}: {path}");
        return 0;
    }

    private static int Verify(Options o)
    {
        var target = o.Positional(0, "folder or file");
        var policy = Policy(o);
        var ledgerPath = o.Value("ledger");
        var ledger = ledgerPath is null ? null : HostLedger.Load(ledgerPath);
        var content = PackReader.Read(target);
        Action<PackStep>? explain = o.Flag("explain") ? Explain : null;
        var result = PackVerifier.Verify(content, policy, DateTimeOffset.UtcNow, ledger, o.Flag("allow-downgrade"), explain);
        if (!result.IsVerified) return Report(result.Problems, "");
        var pack = result.Pack!;
        if (pack.Manifest.Kind == "set")
        {
            // A set verifies as a whole and member by member: each keeps its own list and signatures.
            var (whole, members) = PackVerifier.VerifySet(content, policy, DateTimeOffset.UtcNow, ledger, o.Flag("allow-downgrade"));
            // The set as a whole first: a file outside any member folder, or members other than it names, refuses it.
            if (!whole.IsVerified) return Report(whole.Problems, "");
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
        if (pack.Manifest.Federation is { } federation)
            Console.WriteLine($"  publisher {federation.Publisher}\n  for     {PackFederation.Describe(federation.Audience)}" + (federation.Purpose is { } why ? $"\n  purpose {why}" : ""));
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
            if (content.Files.TryGetValue(PackManifest.FileName, out var manifestBytes) && PackManifest.Parse(manifestBytes).Manifest?.Federation is { } federation)
                Console.WriteLine($"Federated: publisher {federation.Publisher}, for {PackFederation.Describe(federation.Audience)}  (not verified)");
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
        var role = o.Value("role") ?? "publisher";
        if (!TrustPolicy.KeyRoles.Contains(role)) throw new UsageException($"--role is one of {string.Join(", ", TrustPolicy.KeyRoles.Order())}.");
        var key = PackKey.Create();
        Directory.CreateDirectory(folder);
        var protect = o.Value("protect");
        if (protect is not (null or "dpapi")) throw new UsageException("--protect dpapi is the one protection keygen applies; use --signer for a keychain or KMS.");
        if (protect == "dpapi" && !OperatingSystem.IsWindows()) throw new UsageException("--protect dpapi is Windows only.");
        var privatePath = Path.Combine(folder, name + (protect == "dpapi" ? ".key.dpapi" : ".key.pem"));
        if (File.Exists(privatePath)) throw new UsageException($"{privatePath} exists; refusing to overwrite a key.");
        if (protect == "dpapi" && OperatingSystem.IsWindows())
            File.WriteAllBytes(privatePath, System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(key.PrivateKeyPem), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
        else File.WriteAllText(privatePath, key.PrivateKeyPem);
        File.WriteAllText(Path.Combine(folder, name + ".pub.pem"), key.PublicKeyPem);
        var entry = new JsonObject
        {
            ["keyId"] = key.KeyId, ["publicKey"] = key.PublicKeyPem,
            ["idPrefixes"] = new JsonArray((JsonNode)(o.Value("prefix") ?? "io.example")), ["roles"] = new JsonArray((JsonNode)role),
        };
        Console.WriteLine($"Key id  {key.KeyId}\nPrivate {privatePath}  (keep it secret; never commit it)\nTrust-policy entry:\n{Pretty(entry)}");
        // --policy: the entry added to a trust policy file (created when missing), which is then read back as a host reads it.
        if (o.Value("policy") is { } policyPath)
        {
            var policy = File.Exists(policyPath) ? JsonNode.Parse(File.ReadAllText(policyPath))?.AsObject() ?? throw new UsageException($"{policyPath} is not a JSON object.")
                : new JsonObject { ["format"] = TrustPolicy.Format, ["keys"] = new JsonArray() };
            if (policy["keys"] is not JsonArray keys) policy["keys"] = keys = new JsonArray();
            keys.Add(entry.DeepClone());
            var bytes = System.Text.Encoding.UTF8.GetBytes(policy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            TrustPolicy.Parse(bytes);
            File.WriteAllBytes(policyPath, bytes);
            Console.WriteLine($"Added to {policyPath} ({keys.Count} key(s)).");
        }
        return 0;
    }

    // PACK-harden: a signed revocation document (pack format v1.2 §8).
    private static int Revoke(Options o)
    {
        var output = o.Positional(0, "output file");
        if (!PackTime.TryParse(o.Required("expires"), out var expires)) throw new UsageException("--expires is an RFC 3339 date-time, such as 2026-12-31T00:00:00Z.");
        var issuedAt = SigningTime();
        var digests = List(o.Value("digests"));
        var keys = new List<RevokedKey>();
        foreach (var entry in List(o.Value("keys")))
        {
            var at = entry.IndexOf('@', StringComparison.Ordinal);
            DateTimeOffset? compromisedAt = null;
            if (at >= 0)
            {
                if (!PackTime.TryParse(entry[(at + 1)..], out var time)) throw new UsageException($"'{entry}': the time after @ is RFC 3339.");
                compromisedAt = time;
            }
            keys.Add(new RevokedKey(at >= 0 ? entry[..at] : entry, compromisedAt));
        }
        if (digests.Count == 0 && keys.Count == 0) Console.Error.WriteLine("Note: the document revokes nothing; it renews the revocation list's freshness.");
        // Freshness is the newest list's: a short one makes every host that applies it stale when it expires, until a newer list.
        if (expires - issuedAt < TimeSpan.FromDays(1))
            Console.Error.WriteLine($"Note: this list expires {(expires - issuedAt).TotalHours:0.#} h after it is issued; every host that applies it is stale then until a newer list arrives.");
        using var signer = Signer(o);
        File.WriteAllBytes(output, Revocations.Sign(digests, keys, issuedAt, expires, signer));
        Console.WriteLine($"Wrote {output}: {digests.Count} digest(s), {keys.Count} key(s), issued {issuedAt:u}, expires {expires:u}.");
        return 0;
    }

    // Who signs (PACK-toolset): --key <file.pem>, --key env:<VARIABLE> (a PEM in the environment), --key dpapi:<file> (a key
    // protected for this Windows user by keygen --protect dpapi), or --signer "<command>" --public <pub.pem> for any keychain,
    // KMS or HSM: the command reads the bytes to sign on standard input and prints the signature in base64.
    private static PackSigner Signer(Options o)
    {
        if (o.Value("signer") is { } command)
            return PackSigner.FromCommand(command, File.ReadAllText(o.Value("public") ?? throw new UsageException("--signer needs --public <pub.pem>, the key it signs with.")));
        var key = o.Required("key");
        if (key.StartsWith("env:", StringComparison.Ordinal))
            return PackSigner.FromPem(Environment.GetEnvironmentVariable(key[4..]) is { Length: > 0 } pem ? pem
                : throw new UsageException($"The environment variable {key[4..]} holds no key."));
        if (key.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) throw new UsageException("dpapi: keys are Windows only; elsewhere use --signer with the OS keychain.");
            var pem = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(key[6..]), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
            return PackSigner.FromPem(pem);
        }
        return PackSigner.FromPem(File.ReadAllText(key));
    }

    // The reference signer for --signer: what a keychain, KMS or HSM command must do.
    // Publication receipts (federated packs §5): one placement of a federated pack at a URL, signed apart from the pack.
    private static int Receipt(Options o)
    {
        var action = o.Positional(0, "receipt action (sign or verify)");
        var archivePath = o.Positional(1, "pack.ampack");
        var location = o.Required("location");
        var archive = File.ReadAllBytes(archivePath);
        var content = PackReader.Read(archivePath);
        if (action == "verify")
        {
            var policy = Policy(o);
            var verified = PackVerifier.Verify(content, policy, DateTimeOffset.UtcNow);
            if (!verified.IsVerified) return Report(verified.Problems, "");
            var (receipt, problems) = PublicationReceipt.Verify(File.ReadAllBytes(o.Positional(2, "receipt file")), verified.Pack!, archive, location, policy, DateTimeOffset.UtcNow);
            if (receipt is null) return Report(problems, "");
            Console.WriteLine($"Verified the placement of {receipt.Id} {receipt.Version} at {receipt.Location}\n  for     {PackFederation.Describe(receipt.Audience)} ({receipt.Access})"
                + $"\n  probed  denied {string.Join(", ", receipt.Denied)}; allowed {string.Join(", ", receipt.Allowed)}\n  placed  {receipt.PublishedAt:u}");
            return 0;
        }
        if (action != "sign") throw new UsageException("receipt sign or receipt verify.");
        // The publisher's own pack: it must be whole and federated; the signer must be its publisher key.
        var (integrity, list, listBytes, manifest) = PackVerifier.CheckIntegrity(content);
        if (integrity.Count > 0) return Report(integrity, "");
        if (manifest!.Federation is not { } federation) return Report(["The pack is not federated: a receipt is for a pack with a publisher and an audience."], "");
        IReadOnlyList<string>? audience = o.Value("audience") is not { } given ? federation.Audience : given == PackFederation.Public ? null : List(given);
        var access = o.Value("access") ?? (audience is null ? "public" : "wac");
        if (!federation.IsPublic && (audience is null || audience.Any(a => !federation.Audience!.Contains(a))))
            return Report(["The placement's audience is wider than the pack's: a placement can only narrow it."], "");
        var digest = FilesList.DigestOf(listBytes!);
        var receiptValue = new PublicationReceipt(list!.Id, list.Version, digest, location, FilesList.Hex(archive), audience, access,
            List(o.Value("denied")), List(o.Value("allowed")), DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        using var signer = Signer(o);
        var bytes = receiptValue.Sign(signer);
        var outPath = o.Required("out");
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"Receipt {outPath}\n  pack    {list.Id} {list.Version} {digest}\n  at      {location}\n  for     {PackFederation.Describe(audience)} ({access})\n  signed  by {Dsse.Short(signer.KeyId)}");
        return 0;
    }

    private static int SignBytes(Options o)
    {
        using var input = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(o.Required("key")));
        var format = o.Flag("p1363") ? System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            : System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence;
        Console.WriteLine(Convert.ToBase64String(key.SignData(buffer.ToArray(), System.Security.Cryptography.HashAlgorithmName.SHA256, format)));
        return 0;
    }

    private static List<string> List(string? value) =>
        [.. (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // The host's trust policy, with every signed revocation document --revocations names applied, in order (one per revoker,
    // say). With --ledger (or a --store's ledger), what a list revokes is kept by the host, and what the host kept before is
    // added: whatever the command and whatever lists a run is given, nothing revoked here comes back.
    private static TrustPolicy Policy(Options o)
    {
        var policy = TrustPolicy.Parse(File.ReadAllBytes(o.Required("policy")));
        var ledgerPath = LedgerPath(o);
        var ledger = ledgerPath is null ? null : HostLedger.Load(ledgerPath);
        var documents = o.Values("revocations").Select(r => PackReader.ReadArchiveFile(r, Revocations.DocumentLimits)).ToList();
        policy = Revocations.ApplyAll(policy, documents, DateTimeOffset.UtcNow, ledger, out var notApplied);
        foreach (var note in notApplied) Console.Error.WriteLine("Note: " + note);
        if (ledger is not null && documents.Count > 0) ledger.Save(ledgerPath!);
        return policy;
    }

    // The same, as the loader's trust.
    private static AgentMemory.Extensibility.Ampack.PackTrust Trust(Options o)
    {
        var trust = AgentMemory.Extensibility.Ampack.PackTrust.Load(o.Required("policy"));
        var ledger = LedgerPath(o) is { } path ? AgentMemory.Extensibility.Ampack.PackInstallLedger.Open(path) : null;
        var documents = o.Values("revocations").Select(r => PackReader.ReadArchiveFile(r, Revocations.DocumentLimits)).ToList();
        trust = trust.WithRevocations(documents, ledger, out var notApplied);
        foreach (var note in notApplied) Console.Error.WriteLine("Note: " + note);
        return trust;
    }

    // A host that stops using revocation lists forgets the newest list's freshness; what it revoked stays revoked.
    private static int Ledger(Options o)
    {
        var path = LedgerPath(o) ?? throw new UsageException("ledger needs --ledger <ledger.json> or --store <folder>.");
        if (!o.Flag("retire-revocations")) throw new UsageException("ledger takes --retire-revocations.");
        if (o.PositionalFrom(0).Any()) throw new UsageException("ledger takes no file arguments.");
        var ledger = HostLedger.Load(path);
        ledger.RetireRevocationLists();
        ledger.Save(path);
        Console.WriteLine($"Retired the revocation lists' freshness in {path}; {ledger.RevokedDigests.Count} digests and {ledger.RevokedKeys.Count} keys stay revoked.");
        return 0;
    }

    private static string? LedgerPath(Options o) => o.Value("ledger") ?? (o.Value("store") is { } store ? Path.Combine(store, "ledger.json") : null);

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
        using var setSigner = Signer(o);
        PackBuilder.Sign(files, setSigner, o.Value("role") ?? "publisher", SigningTime());
        File.WriteAllBytes(output, PackBuilder.Pack(files, compress: !o.Flag("stored")));
        Console.WriteLine($"Set {id} {version}: {members.Count} members\n  digest  {FilesList.DigestOf(files[PackPaths.FilesList])}\n  wrote   {output}");
        return 0;
    }

    // PACK-serve: the packs mounted as a host mounts them, served read-only over MCP.
    private static int ServePacks(Options o)
    {
        var trust = Trust(o);
        var archives = Archives(o, "serve");
        try
        {
            var mount = AgentMemory.Extensibility.Ampack.PackMemory.Mount(archives, trust);
            return o.Value("http") is { } url ? Rest.Run(mount, url, o.Flag("allow-remote")) : Serve.Run(mount);
        }
        catch (AgentMemory.Extensibility.Ampack.PackLoadException e) { return Report(e.Problems, ""); }
    }

    // PACK-toolset: a pack's own tests, on the packs loaded as a host loads them and held in memory.
    private static int Test(Options o)
    {
        var trust = Trust(o);
        var archives = Archives(o, "test");
        try
        {
            var (loaded, memory) = AgentMemory.Extensibility.Ampack.PackMemory.Mount(archives, trust);
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

    // The packs a command runs on: the files it names, and with --store every pack installed there.
    private static List<byte[]> Archives(Options o, string command)
    {
        var archives = o.PositionalFrom(0).Select(p => PackReader.ReadArchiveFile(p)).ToList();
        if (o.Value("store") is { } store) archives.AddRange(AgentMemory.Extensibility.Ampack.PackStore.Open(store).Archives());
        return archives.Count > 0 ? archives : throw new UsageException($"{command} needs at least one pack, or --store with packs installed.");
    }

    // PACK-toolset: a host's pack store.
    private static int Install(Options o)
    {
        var path = o.Positional(0, "pack to install");
        var store = AgentMemory.Extensibility.Ampack.PackStore.Open(o.Required("store"));
        try
        {
            var installed = store.Install(PackReader.ReadArchiveFile(path), Trust(o), o.Flag("allow-downgrade"));
            Console.WriteLine($"Installed {installed.Id} {installed.Version} ({installed.Kind})\n  digest  {installed.Digest}\n  file    {installed.File}");
            return 0;
        }
        catch (AgentMemory.Extensibility.Ampack.PackLoadException e) { return Report(e.Problems, ""); }
    }

    private static int Uninstall(Options o)
    {
        var id = o.Positional(0, "pack id");
        var store = AgentMemory.Extensibility.Ampack.PackStore.Open(o.Required("store"));
        if (!store.Uninstall(id)) return Report([$"{id} is not installed."], "");
        Console.WriteLine($"Removed {id}; the store's ledger keeps it.");
        return 0;
    }

    private static int ListInstalled(Options o)
    {
        var store = AgentMemory.Extensibility.Ampack.PackStore.Open(o.Required("store"));
        if (store.Installed.Count == 0) Console.WriteLine("No packs installed.");
        foreach (var p in store.Installed) Console.WriteLine($"  {p.Id} {p.Version} ({p.Kind})  {p.Digest[..23]}…  installed {p.InstalledAt:u}");
        return 0;
    }

    // PACK-toolset: ampack diff.
    private static int DiffPacks(Options o)
    {
        var (first, second) = (o.Positional(0, "first pack"), o.Positional(1, "second pack"));
        if (o.Value("policy") is not null)
        {
            var policy = Policy(o);
            foreach (var path in new[] { first, second })
                if (PackVerifier.Verify(PackReader.Read(path), policy, DateTimeOffset.UtcNow) is { IsVerified: false } refused)
                    return Report(refused.Problems.Select(p => $"{path}: {p}"), "");
        }
        AgentMemory.Extensibility.Ampack.PackDiff diff;
        try { diff = AgentMemory.Extensibility.Ampack.PackDiff.Compare(first, second); }
        catch (AgentMemory.Extensibility.Ampack.PackLoadException e) { return Report(e.Problems, ""); }
        if (o.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                before = new { id = diff.Before.Id, version = diff.Before.Version, digest = diff.Before.Digest },
                after = new { id = diff.After.Id, version = diff.After.Version, digest = diff.After.Digest },
                same = diff.Same, declarations = diff.Declarations, records = diff.Records, unchangedRecords = diff.UnchangedRecords, files = diff.Files,
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } }));
            return 0;
        }
        static string Mark(AgentMemory.Extensibility.Ampack.PackChange c) => c switch
        {
            AgentMemory.Extensibility.Ampack.PackChange.Added => "+", AgentMemory.Extensibility.Ampack.PackChange.Removed => "-", _ => "~",
        };
        Console.WriteLine($"{diff.Before.Id} {diff.Before.Version} -> {diff.After.Id} {diff.After.Version}{(diff.Same ? "  (the same pack)" : "")}");
        Console.WriteLine($"  digest  {diff.Before.Digest} -> {diff.After.Digest}");
        foreach (var group in diff.Declarations.GroupBy(d => d.File))
        {
            Console.WriteLine($"  {group.Key}");
            foreach (var change in group)
                Console.WriteLine($"    {Mark(change.Change)} {change.Path}: " + change.Change switch
                {
                    AgentMemory.Extensibility.Ampack.PackChange.Added => Short(change.After!),
                    AgentMemory.Extensibility.Ampack.PackChange.Removed => Short(change.Before!),
                    _ => $"{Short(change.Before!)} -> {Short(change.After!)}",
                });
        }
        var counts = diff.Records.GroupBy(r => r.Change).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"  records  +{counts.GetValueOrDefault(AgentMemory.Extensibility.Ampack.PackChange.Added)} " +
            $"-{counts.GetValueOrDefault(AgentMemory.Extensibility.Ampack.PackChange.Removed)} ~{counts.GetValueOrDefault(AgentMemory.Extensibility.Ampack.PackChange.Changed)}, {diff.UnchangedRecords} unchanged");
        foreach (var record in diff.Records.Take(50))
            Console.WriteLine($"    {Mark(record.Change)} {record.Type} '{record.Key}'" + (record.Change == AgentMemory.Extensibility.Ampack.PackChange.Changed
                ? ": " + string.Join(", ", record.Properties) + (record.LinksChanged ? (record.Properties.Count > 0 ? ", links" : "links") : "") : ""));
        if (diff.Records.Count > 50) Console.WriteLine($"    ... and {diff.Records.Count - 50} more (--json lists all)");
        if (diff.Files.Count > 0)
        {
            Console.WriteLine("  files");
            foreach (var file in diff.Files) Console.WriteLine($"    {Mark(file.Change)} {file.Path} ({file.Before?.ToString("N0") ?? "-"} -> {file.After?.ToString("N0") ?? "-"} bytes)");
        }
        return 0;
    }

    private static string Short(string json) => json.Length <= 80 ? json : json[..77] + "...";

    // verify --explain: one line per step, then its details.
    private static void Explain(PackStep step)
    {
        Console.WriteLine($"{(step.Passed ? "  ok  " : "  FAIL")}  {step.Name}");
        foreach (var line in step.Lines) Console.WriteLine($"          {line}");
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
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "stored", "record", "allow-downgrade", "explain", "json", "p1363", "allow-remote", "retire-revocations", "allow-loopback-http", "no-recheck" };
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
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
            if (!options._values.TryGetValue(name, out var values)) options._values[name] = values = [];
            values.Add(list[++i]);
        }
        return options;
    }

    internal string Positional(int index, string what) =>
        index < _positional.Count ? _positional[index] : throw new UsageException($"Missing the {what}.");

    internal IEnumerable<string> PositionalFrom(int index) => _positional.Skip(index);
    internal string? Value(string name) => _values.TryGetValue(name, out var values) ? values[^1] : null;
    /// <summary>Every value an option was given, in order (an option given more than once, such as --revocations).</summary>
    internal IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var values) ? values : [];
    internal string Required(string name) => Value(name) ?? throw new UsageException($"Missing --{name}.");
    internal bool Flag(string name) => _flags.Contains(name);
}
