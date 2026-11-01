using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace Ampack;

/// <summary>
/// <c>ampack</c>: the memory pack tool. Every command that reads a pack verifies it first; nothing is written into a store
/// here. Exit codes: 0 done, 1 refused (the reasons are printed), 2 wrong usage, 3 the environment is busy (a ledger another
/// run holds, a port in use), 4 an unexpected error.
/// </summary>
internal static partial class Program
{
    private const string Usage = """
        ampack <command> [options]          ampack <command> --help          ampack --version
        Exit codes: 0 done; 1 refused (a pack, a file or a check that does not hold); 2 usage (including an option the
        command does not take); 3 busy (the ledger is held by another run, or the port is in use); 4 an unexpected error.

          init <folder> --id <reverse.dns.id> [--kind knowledge|definition] [--version 0.1.0] [--owner <name>]
               [--publisher <WebID> --audience public|<WebID,…> [--purpose <text>]]
                                 start a pack folder (pack.json, a pack card, an ontology, a view, .gitattributes, and
                                 for a knowledge pack one record); with --publisher, a federated pack for that audience
          lint <folder|file> [--policy <trust.json>] [--safety]
                                 check paths, the JSON profile, pack.json and, once built, the files list; with
                                 --policy, load a .ampack as a host does and give advice on what a host will do with
                                 it later (no trial, no upgrade rule, links that need consent, validity ending soon);
                                 with --safety, and always for a curated or premium pack, every scan finding is an error
          build <folder> [--out <file.ampack>] [--key <private.pem>] [--role publisher] [--stored]
                                 write META/files.json, optionally sign, and pack; prints the digest
          sign <folder> --key <private.pem> [--role publisher|curator|assurer]
          verify <folder|file> --policy <trust.json> [--revocations <file>] [--ledger <ledger.json>] [--record] [--allow-downgrade]
                 [--explain]       with --explain, every step in order: what it checked, whether it held, and why
          inspect <folder|file>  identity, digest, files and signatures, without trusting them
          diff <a> <b> [--policy <trust.json>] [--json]
                                 what changed between two packs: identity, every declared value (ontology, views,
                                 walk, trial, ...), every record by type and key, every file; --policy verifies both first
          keygen --out <folder> [--name <name>] [--prefix <id.prefix>] [--role publisher|curator|assurer|evaluator|revoker]
                 [--protect dpapi | --passphrase-env <VARIABLE>] [--policy <trust.json>]
                                 a P-256 key pair and a trust-policy entry for it; --protect dpapi keeps the
                                 private key encrypted for this Windows user (use it as --key dpapi:<file>);
                                 --passphrase-env writes it encrypted (PKCS#8, AES-256) with the passphrase in that
                                 variable, and every command then opens it with AMPACK_KEY_PASSPHRASE; the private key
                                 file is readable by its owner only; --policy adds the key to a trust policy file
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
          lock <composite-folder> <member.ampack>... --policy <trust.json> [--revocations <file>]
                                 pin every member's id, version and digest in META/lock.json
          migrate <bundle-folder|manifest.json> --out <folder>
                                 turn a module bundle (manifest.json, seed files, icon) into a v1.2 pack folder:
                                 a knowledge pack when it seeds records, a definition pack when it only declares,
                                 a composite pack for a composite manifest (then: ampack lock, ampack build)
          set <out.ampack> <member.ampack>... --id <id> --version <v> --key <private.pem> --policy <trust.json> [--revocations <file>]
                                 one archive carrying complete packs side by side, signed as a whole
          install <pack.ampack> --store <folder> --policy <trust.json> [--revocations <file>] [--allow-downgrade]
                                 verify a pack or pack set in full and install it in a host's pack store; the
                                 store's ledger refuses an older version or other content under a known version
          uninstall <id> --store <folder>
                                 remove an installed pack (the ledger keeps it)
          list --store <folder>  the installed packs
          serve [<pack.ampack>...] [--store <folder>] --policy <trust.json> [--revocations <file>]
                [--http <http://localhost:5199/> [--allow-remote]] [--activations <folder> --host-key <host.pub.pem>]
                [--grant <module>=<into>]...
                                 mount the packs in memory and serve them over MCP on standard input and output:
                                 pack_list, pack_ask (a view: search text or a key), pack_walk (from files and symbols);
                                 with --http, the same over REST: GET /v1/meta, GET /v1/packs,
                                 POST /v1/packs/{id}/views/{view}/ask, POST /v1/walk (this machine only, unless
                                 --allow-remote: then every request sends Authorization: Bearer $AMPACK_SERVE_TOKEN,
                                 packs published for named WebIDs stay local, and TLS comes from a proxy in front;
                                 on this machine too, packs published for named WebIDs need $AMPACK_SERVE_TOKEN);
                                 with --activations, packs the host's trial gate keeps off are not served; with
                                 --grant, the host's consent grants, as GrantConsent gives them to a host
          evidence --activations <folder> --host-key <host.pub.pem> --pack <id> [--version <v>] --out <run-folder>
                                 a host's trial decision for a pack (the latest, or of that version) as an AEF run:
                                 the pack as its subject, the trial's tasks with and without it, the gate's decision,
                                 and the signed record itself; an assurer seals and signs it (agenteval aef seal
                                 --sealed-by ingest --key ...)
          scan <folder|file> [--policy <trust.json>] [--aef <run-folder>]
                                 look in every record and declaration for personal data, secrets and prompt-injection
                                 payloads; exit 1 when anything is found; with --policy, scan only a pack that
                                 verifies; with --aef, write the scan as an AEF run for an assurer to seal and sign
          countersign <pack.ampack> --key <file|env:VAR|dpapi:FILE> --role <curator|assurer|publisher> --out <file> --policy <trust.json> [--revocations <file>]
                                 sign a built pack again in another role, its content and digest unchanged
          receipt sign <pack.ampack> --location <url> --key <...> --out <file> [--audience public|<WebID,…>]
                 [--access wac|acp|public] [--denied anonymous,<WebID>,…] [--allowed <WebID>,…]
                                 a publication receipt for a federated pack placed at a URL: that URL, those exact
                                 bytes, an audience no wider than the pack's (default: the pack's), and what was probed
          receipt verify <pack.ampack> <receipt> --location <url> --policy <trust.json> [--revocations <file>]
                                 verify the pack and its receipt as a host fetching it from that URL does
          certify <pack.ampack> --policy <trust.json> --activations <folder> --host-key <its gate's host.pub.pem>
                  --key <...> --evaluator <name> --task-source <text> [--aef <run-folder>] --out <file.ampack>
                                 as an evaluator: sign an evaluation certificate from your own trial decision for exactly
                                 this pack (evaluate --gate), with the AEF run beside it (ampack evidence, sealed by
                                 AgentEval), and add it to the pack; its digest does not change
          certificate verify <pack.ampack> --policy <trust.json> [--certificate META/attestations/<name> | <file>]
                                 verify the pack and its certificates as a host does: a key the policy names as
                                 evaluator for the pack's id, this exact pack, and the AEF run's hash when it travels with it
          erase <pack id> --erased Type=key [--erased ...] [--reason withdrawn|incorrect|harmful|legal] [--plain-keys] --key <publisher.pem> --out <file>
                                 build and sign an erasure (feature 08): each key as its salted hash unless --plain-keys
          erasure verify <pack.ampack> --policy <trust.json> [--erasure META/attestations/<name> | <file>]
                                 check an erasure (feature 08) for the pack: by a key trusted as publisher or revoker for its id
          erasure receipt <erasure file> --policy <trust.json> --key <host.pem> --out <file>
                                 sign this host's receipt that it applied the erasure, naming nothing else
          admit <pack.ampack> --policy <trust.json> --host-signing-key <host.key.pem> --activations <folder>
                                 for a host that trusts certificates for this pack's namespace: record a decision from the
                                 latest trusted certificate covering the trial the pack declares, signed with the host's
                                 key; it counts only where the host composes with TrustCertificatesFor (proven here,
                                 the host's own trial, is the default everywhere else)
          gate <pack.ampack>... --policy <trust.json> --activations <folder> --host-key <host.pub.pem>
               [--require-trials-for <prefix,…>] [--trust-certificates-for <prefix,…>]
                                 what this host's trial gate does with the packs, from its signed decisions alone: on or
                                 off, and why (as a module set composing with RequireTrials); no store, no trial run
          trust add --public <pub.pem> --policy <trust.json> --prefix <id.prefix,...> [--role <role,...>] [--webid <WebID>]
                                 trust a key you checked with its owner, for those ids and roles (publisher by default)
          trust list --policy <trust.json>
                                 the keys a policy trusts: id, roles, prefixes, WebID, expiry; and what it revokes
          trust remove <keyId> --policy <trust.json>
                                 stop trusting a key on this host (to refuse what it signed everywhere, revoke it)
          trust webid <WebID> [--key <keyId> --policy <trust.json> --prefix <id.prefix> [--role publisher] [--host-webid <WebID>]]
                                 list the P-256 keys a WebID's profile asserts; with --key, bind that key to the WebID
                                 in the trust policy (never trust on first use: name the key you checked)
          solid bind-key --webid <WebID> --public <pub.pem>
                                 assert a signing key in your own WebID profile (sec:assertionMethod, a P-256 JWK)
          solid publish <pack.ampack> --webid <WebID> --key <...> [--audience <WebID,…>] [--probe-as <WebID>]
                [--probe-stranger <WebID>]
                                 put a federated pack in your Pod for its audience (WAC or ACP, whichever the server
                                 uses), probe that anonymous is refused (that --probe-as reads the exact bytes, that
                                 --probe-stranger is refused), sign the receipt, update the catalogs and the type index;
                                 anything that does not hold withdraws what was placed
          solid discover --from <publisher WebID> [--webid <WebID>]
                                 what a publisher offers you (or anyone): from its profile's type index to its catalogs
          solid fetch --from <publisher WebID> --webid <WebID> --policy <trust.json> --store <folder> [--id <pack id>]
                [--no-recheck] [--allow-downgrade]
                                 fetch, re-check the publisher's key in its profile, verify pack and receipt as this host,
                                 install, and log where it came from (<store>/federation.jsonl)
          Solid commands log in with client credentials from AMPACK_SOLID_CLIENT_ID and AMPACK_SOLID_CLIENT_SECRET (a probe
          identity's from AMPACK_PROBE_CLIENT_ID and AMPACK_PROBE_CLIENT_SECRET, a stranger's from AMPACK_STRANGER_CLIENT_ID and
          AMPACK_STRANGER_CLIENT_SECRET); the token must name the WebID given; --allow-loopback-http for a local server.
          test [<pack.ampack>...] [--store <folder>] --policy <trust.json> [--revocations <file>]
                                 load the packs as a host does (every record checked), hold them in memory, and run
                                 each pack's own tests (evals/tests.json): views, searches and walks, offline;
                                 give a snapshot's definition pack beside it
        """;

    internal static int Main(string[] args)
    {
        // Messages name packs, digests and paths with non-ASCII characters (…, ≥); write them as UTF-8 on every console.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // Every line goes through one writer that shows control and bidirectional characters as escapes (review 7, D3).
        if (Console.Out is not ShownWriter) Console.SetOut(new ShownWriter(Console.Out));
        if (Console.Error is not ShownWriter) Console.SetError(new ShownWriter(Console.Error));
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
        if (args[0] is "--version" or "version") { Console.WriteLine($"ampack {ToolVersion()} (pack format {PackManifest.Format})"); return 0; }
        try
        {
            var options = Options.Parse(args.Skip(1));
            if (options.Flag("help") || args.Skip(1).Contains("-h")) return CommandHelp(args[0]);
            CheckOptions(args[0], options);
            CheckPositionals(args[0], options);
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
                "certify" => Certify(options),
                "certificate" => CertificateCommand(options),
                "erasure" => ErasureCommand(options),
                "aggregate" => AggregateCommand(options),
                "import" => Import(options),
                "erase" => Erase(options),
                "admit" => Admit(options),
                "gate" => Gate(options),
                "solid" => Solid(options),
                "trust" => TrustCommand(options),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}", 2),
            };
        }
        catch (UsageException e) { return Fail(e.Message, 2); }
        // A ledger another run holds is an operational failure, not a refusal (seventh review).
        catch (LedgerBusyException e) { return Fail(e.Message, 3); }
        // A port another process holds is the environment, not a refusal of anything given (review 2).
        catch (System.Net.HttpListenerException e) { return Fail($"The server could not start: {Shown(e.Message, 1000)}", 3); }
        // Input that does not hold (a malformed file, a link header, a URL, a value of the wrong JSON type) is a refusal.
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or System.Security.Cryptography.CryptographicException
            or HttpRequestException or TaskCanceledException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return Fail(Shown(e.Message, 4000), 1);
        }
        // Anything else is a defect: said plainly, with no stack trace, and an exit code of its own (review 1, L10).
        catch (Exception e) { return Fail($"ampack failed unexpectedly ({e.GetType().Name}): {Shown(e.Message, 1000)}", 4); }
    }

    // ampack <command> --help: the usage lines for that command.
    private static int CommandHelp(string command)
    {
        var lines = Usage.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("  " + command + " ", StringComparison.Ordinal) || l.TrimEnd() == "  " + command);
        if (start < 0) return Fail($"Unknown command '{command}'.\n\n{Usage}", 2);
        var end = start + 1;
        while (end < lines.Length && (lines[end].StartsWith("       ", StringComparison.Ordinal) || lines[end].StartsWith("  " + command + " ", StringComparison.Ordinal))) end++;
        Console.WriteLine(string.Join('\n', lines[start..end]).TrimEnd());
        Console.WriteLine($"\nOptions: {string.Join(", ", CommandOptions[command].Order(StringComparer.Ordinal).Select(n => "--" + n))}");
        return 0;
    }

    // The library's version, which is what decides: the format, the verifier and the gate (without build metadata).
    private static string ToolVersion() =>
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(PackManifest).Assembly)
            ?.InformationalVersion ?? "unknown").Split('+')[0];

    // Review 1, H2: an option a command does not take is a usage error, never ignored, so a typo cannot drop a trust input
    // (--revocation for --revocations, --legder for --ledger). Shared helpers read the same names everywhere.
    private static readonly string[] SignerOptions = ["key", "signer", "public"], TrustOptions = ["policy", "revocations"];
    private static readonly Dictionary<string, HashSet<string>> CommandOptions = new Dictionary<string, string[]>
    {
        ["init"] = ["id", "version", "kind", "owner", "publisher", "audience", "purpose"],
        ["lint"] = ["safety", .. TrustOptions],
        ["build"] = ["out", "role", "stored", .. SignerOptions],
        ["sign"] = ["role", .. SignerOptions],
        ["verify"] = ["ledger", "record", "allow-downgrade", "explain", .. TrustOptions],
        ["inspect"] = [],
        ["diff"] = ["json", .. TrustOptions],
        ["keygen"] = ["out", "name", "prefix", "role", "protect", "passphrase-env", "policy"],
        ["revoke"] = ["digests", "keys", "expires", .. SignerOptions],
        ["ledger"] = ["ledger", "store", "retire-revocations"],
        ["sign-bytes"] = ["key", "p1363"],
        ["lock"] = [.. TrustOptions],
        ["migrate"] = ["out"],
        ["set"] = ["id", "version", "role", "stored", .. SignerOptions, .. TrustOptions],
        ["test"] = ["store", .. TrustOptions],
        ["evidence"] = ["activations", "host-key", "pack", "version", "out"],
        ["scan"] = ["aef", .. TrustOptions],
        ["countersign"] = ["out", "role", .. SignerOptions, .. TrustOptions],
        ["serve"] = ["store", "http", "allow-remote", "grant", "client-endpoint", "print-config", .. GateOptions, .. TrustOptions],
        ["install"] = ["store", "allow-downgrade", .. TrustOptions],
        ["uninstall"] = ["store"],
        ["list"] = ["store"],
        ["receipt"] = ["location", "audience", "access", "denied", "allowed", "out", .. SignerOptions, .. TrustOptions],
        ["certify"] = ["activations", "host-key", "evaluator", "task-source", "aef", "tool", "tool-version", "out", .. SignerOptions, .. TrustOptions],
        ["certificate"] = ["certificate", .. TrustOptions],
        ["erasure"] = ["erasure", "out", .. SignerOptions, .. TrustOptions],
        ["aggregate"] = ["aggregate", "out", "activations", "host-key", "key", "from", "to", .. TrustOptions],
        ["import"] = ["id", "version", "owner", "key", "out", "mapping", "source", "git"],
        ["erase"] = ["erased", "reason", "plain-keys", "out", .. SignerOptions],
        ["admit"] = ["host-signing-key", "activations", .. TrustOptions],
        ["gate"] = [.. GateOptions, .. TrustOptions],
        ["solid"] = ["allow-loopback-http", "webid", "audience", "probe-as", "probe-stranger", "from", "id", "no-recheck", "store", "allow-downgrade", .. SignerOptions, .. TrustOptions],
        ["trust"] = ["allow-loopback-http", "key", "prefix", "role", "host-webid", "policy", "public", "webid"],
    }.ToDictionary(c => c.Key, c => new HashSet<string>(c.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    // The most arguments each command takes besides its options; absent means any number (review 2: `ampack verify a b`
    // checked a and ignored b).
    private static readonly Dictionary<string, int> MaxPositionals = new(StringComparer.Ordinal)
    {
        ["init"] = 1, ["lint"] = 1, ["build"] = 1, ["sign"] = 1, ["verify"] = 1, ["inspect"] = 1, ["diff"] = 2, ["keygen"] = 0,
        ["revoke"] = 1, ["ledger"] = 0, ["sign-bytes"] = 0, ["migrate"] = 1, ["evidence"] = 0, ["scan"] = 1, ["countersign"] = 1,
        ["install"] = 1, ["uninstall"] = 1, ["list"] = 0, ["receipt"] = 3, ["certify"] = 1, ["certificate"] = 2, ["erasure"] = 2, ["aggregate"] = 2, ["import"] = 2, ["erase"] = 1, ["admit"] = 1,
        ["solid"] = 2, ["trust"] = 2,
    };

    // Per subaction where a command has them (review 3: the command's largest count let `receipt sign a b` through).
    private static readonly Dictionary<string, int> MaxSubactionPositionals = new(StringComparer.Ordinal)
    {
        ["receipt sign"] = 2, ["receipt verify"] = 3, ["certificate verify"] = 2, ["solid bind-key"] = 1, ["solid publish"] = 2,
        ["solid discover"] = 1, ["solid fetch"] = 1, ["trust add"] = 1, ["trust list"] = 1, ["trust remove"] = 2, ["trust webid"] = 2,
    };

    private static void CheckPositionals(string command, Options options)
    {
        if (options.PositionalCount > 0 && MaxSubactionPositionals.TryGetValue(command + " " + options.Positional(0, "action"), out var forAction))
        {
            if (options.PositionalCount > forAction)
                throw new UsageException($"{command} {options.Positional(0, "action")} takes at most {forAction - 1} argument{(forAction == 2 ? "" : "s")} besides its options; "
                    + $"{options.PositionalCount - 1} were given.");
            return;
        }
        if (MaxPositionals.TryGetValue(command, out var most) && options.PositionalCount > most)
            throw new UsageException($"{command} takes {(most == 0 ? "no arguments" : $"at most {most} argument{(most == 1 ? "" : "s")}")} besides its options; "
                + $"{options.PositionalCount} were given. ampack {command} --help shows them.");
    }

    private static void CheckOptions(string command, Options options)
    {
        if (!CommandOptions.TryGetValue(command, out var allowed)) return; // an unknown command is reported by the dispatch
        foreach (var name in options.Names.Where(n => n != "help" && !allowed.Contains(n)))
        {
            var closest = allowed.MinBy(a => Distance(a, name));
            throw new UsageException($"ampack {command} takes no --{name}"
                + (closest is not null && Distance(closest, name) <= 3 ? $"; did you mean --{closest}?" : ".")
                + (allowed.Count > 0 ? $" Its options: {string.Join(", ", allowed.Order(StringComparer.Ordinal).Select(n => "--" + n))}." : " It takes no options."));
        }
    }

    // Edit distance, for suggesting the option a typo meant.
    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return row[b.Length];
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
        // --publisher: a federated pack (federated packs §3), its extension written critical; it is checked as a host reads it.
        if (o.Value("publisher") is { } publisher)
        {
            var audience = o.Value("audience") ?? throw new UsageException("--publisher needs --audience: public, or the WebIDs it is for (comma-separated).");
            var federation = new JsonObject
            {
                ["format"] = PackFederation.Format, ["publisher"] = publisher,
                ["audience"] = audience == PackFederation.Public ? PackFederation.Public : new JsonArray([.. List(audience).Select(a => (JsonNode)a)]),
            };
            if (o.Value("purpose") is { } purpose) federation["purpose"] = purpose;
            manifest["extensions"] = new JsonObject { ["critical"] = new JsonArray((JsonNode)PackFederation.ExtensionName), [PackFederation.ExtensionName] = federation };
            if (PackManifest.Parse(System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString())).Problem is { } problem) throw new UsageException(problem);
        }
        else if (o.Value("audience") is not null || o.Value("purpose") is not null) throw new UsageException("--audience and --purpose belong to a federated pack: give --publisher too.");
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
        // The pack card (§15): who owns it, what it is for and not for; lint advises until the template texts are replaced.
        var card = new JsonObject
        {
            ["format"] = PackCard.Format,
            ["owner"] = new JsonObject { ["name"] = o.Value("owner") ?? AgentMemory.Extensibility.Ampack.PackCardInfo.TemplateOwner },
            ["intendedUse"] = AgentMemory.Extensibility.Ampack.PackCardInfo.TemplateUse,
            ["limitations"] = AgentMemory.Extensibility.Ampack.PackCardInfo.TemplateLimitations,
            ["boundaries"] = new JsonArray(), ["risks"] = new JsonArray(),
        };
        if (PackCard.Parse(System.Text.Encoding.UTF8.GetBytes(card.ToJsonString())).Problem is { } cardProblem) throw new UsageException(cardProblem);
        Write(folder, PackManifest.FileName, Pretty(manifest));
        Write(folder, PackCard.FileName, Pretty(card));
        Write(folder, PackLayout.Ontology, Pretty(ontology));
        Write(folder, PackLayout.Views, Pretty(views));
        if (kind == "knowledge") Write(folder, "records/note.jsonl", record.ToJsonString() + "\n");
        Write(folder, ".gitattributes", PackBuilder.GitAttributes);
        Console.WriteLine($"Started {id} {version} in {folder}. Next: fill in card.json, then ampack build {folder} --key <private.pem> (a host loads only a signed pack).");
        return 0;
    }

    private static int Migrate(Options o)
    {
        var source = o.Positional(0, "bundle folder or manifest.json");
        var output = o.Required("out");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new UsageException($"{output} is not empty.");
        IReadOnlyDictionary<string, byte[]> bundle;
        if (File.Exists(source))
            bundle = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PackLayout.ModuleManifestFile] = PackReader.ReadFile(source, PackLimits.Default.MaxFileBytes, "The manifest") };
        else
        {
            var root = new DirectoryInfo(source);
            if (!root.Exists) throw new UsageException($"No bundle at {source}.");
            bundle = root.EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.Name.StartsWith("agentmemory.module.", StringComparison.Ordinal))
                .ToDictionary(f => Path.GetRelativePath(root.FullName, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), f => PackReader.ReadFile(f.FullName, PackLimits.Default.MaxFileBytes, "A bundle file"), StringComparer.Ordinal);
        }
        var files = PackLayout.FromModule(bundle);
        foreach (var (path, bytes) in files) Write(output, path, bytes);
        Write(output, ".gitattributes", PackBuilder.GitAttributes);
        var pack = JsonNode.Parse(files[PackManifest.FileName])!;
        // A bundle's own text, shown, never trusted (review 9, A7: a line feed in its id printed forged lines).
        Console.WriteLine($"Migrated {Shown(pack["id"]?.ToString() ?? "", 200)} {Shown(pack["version"]?.ToString() ?? "", 80)} as a {Shown(pack["kind"]?.ToString() ?? "", 40)} pack: {files.Count} files in {output}. Next: ampack build {output} --key <key>");
        return 0;
    }

    private static int Lint(Options o)
    {
        var content = PackReader.Read(o.Positional(0, "folder or file"));
        // Lint reads what nobody verified yet: the one bound for such content comes before any JSON is parsed (review 8, A1: a
        // 60 MiB line cost 1 GiB here, while diff, scan and inspect refused it bounded).
        if (PackContentBounds.Problems(content.Files, verified: false) is { Count: > 0 } bounds) return Report(bounds, "");
        var problems = PackPaths.Problems(content.Files.Keys).ToList();
        foreach (var (path, bytes) in content.Files.Where(f => FilesList.IsListed(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var problem = path.EndsWith(".jsonl", StringComparison.Ordinal) ? PackJson.CheckLines(bytes)
                : path.EndsWith(".json", StringComparison.Ordinal) ? PackJson.Check(bytes) : null;
            if (problem is not null) problems.Add($"'{path}' {problem}.");
        }
        PackManifest? declared = null;
        if (content.Files.TryGetValue(PackManifest.FileName, out var manifest))
        {
            // What build would refuse, lint says (review 7, F2): the writer's own rules, beside the format's.
            var (parsed, problem) = PackManifest.Parse(manifest);
            declared = parsed;
            if (problem is not null) problems.Add(problem);
            else if (PackBuilder.WriterProblem(parsed!) is { } refused) problems.Add(refused);
        }
        else problems.Add($"There is no {PackManifest.FileName}.");
        problems.AddRange(PackPaths.SignatureFolderProblems(content.Files));
        if (content.Files.TryGetValue(PackCard.FileName, out var cardBytes) && PackCard.Parse(cardBytes).Problem is { } cardProblem) problems.Add(cardProblem);
        if (problems.Count == 0 && content.Files.ContainsKey(PackPaths.FilesList)) problems.AddRange(PackVerifier.CheckIntegrity(content).Problems);
        // The content scan as an error level (feature 04, R-HOST-057): asked for, or for a curated or premium pack, every
        // high-severity finding (a secret, a card or account number, an injection payload) stops it, before the pack travels; an
        // address or a phone number, which a pack may mean to carry (its owner's contact), stays the scan's advice.
        if (problems.Count == 0 && (o.Flag("safety") || declared?.Tier is "curated" or "premium"))
        {
            try
            {
                foreach (var finding in AgentMemory.Extensibility.Ampack.PackScan.Scan(content.Files, verified: false).Findings.Where(f => f.Severity == "high"))
                    problems.Add(Shown($"safety: {finding.Rule} ({finding.Severity}) in {finding.File}{(finding.Line > 0 ? ":" + finding.Line : "")} {finding.Path}", 500));
            }
            catch (AgentMemory.Extensibility.Ampack.PackLoadException e) { problems.AddRange(e.Problems); }
        }
        if (problems.Count > 0 || o.Value("policy") is null) return Report(problems, "Lint passed.");
        // Advice needs the pack as a host loads it: a built .ampack, against the host's trust.
        var target = o.Positional(0, "folder or file");
        if (!File.Exists(target)) throw new UsageException("Advice (--policy) reads a built .ampack; build the folder first.");
        var inspection = AgentMemory.Extensibility.Ampack.PackInspection.Inspect(PackReader.ReadArchiveFile(target), Trust(o));
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
        if (dropped) Console.WriteLine("The content changed, so the old signatures and certificates were removed.");
        if (o.Value("key") is not null || o.Value("signer") is not null)
        {
            using var signer = Signer(o);
            PackBuilder.Sign(files, signer, o.Value("role") ?? "publisher", SigningTime());
        }
        // The writer's read-back before the folder is touched (review 9, A6: the new envelope reached the folder first, and a
        // refused archive left it holding what lint refuses).
        var archive = PackBuilder.Pack(files, compress: !o.Flag("stored"));
        WriteMeta(folder, files);
        var digest = FilesList.DigestOf(files[PackPaths.FilesList]);
        var output = o.Value("out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder))!, $"{integrity.List!.Id}-{integrity.List.Version}{PackReader.Extension}");
        File.WriteAllBytes(output, archive);
        Console.WriteLine($"{integrity.List!.Id} {integrity.List.Version}\n  digest  {digest}\n  files   {integrity.List.Files.Count}\n  signed  {files.Keys.Count(PackPaths.IsEnvelope)}\n  wrote   {output}");
        return 0;
    }

    // PACK-evidence-aef: a host's trial decision as an AEF run.
    private static int Evidence(Options o)
    {
        var records = new AgentMemory.Extensibility.Modules.PackActivationFolder(o.Required("activations"), Text(o.Required("host-key"), "The host key"));
        var id = o.Required("pack");
        var version = o.Value("version");
        // The host's own trial, the latest: never an admission it made from someone else's certificate (review 2).
        var decision = records.History(id).Where(d => d.Basis is null && (version is null || d.Version == version)).MaxBy(d => d.DecidedAt);
        if (decision is null) return Report([$"No decision signed by this host for {id}{(version is null ? "" : " " + version)} in {o.Required("activations")}."], "");
        var file = records.FileOf(decision);
        var runId = AgentMemory.Extensibility.Ampack.PackTrialEvidence.WriteRun(decision, o.Required("out"), file is null ? null : PackReader.ReadFile(file, PackAttestations.MaximumRunBytes, "The evidence file"),
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
        Console.WriteLine($"{id} {decision.Version}: {(decision.Active ? "on" : "off")}, {decision.Reason}\n  run     {runId}\n  wrote   {o.Required("out")}");
        return 0;
    }

    // PACK-scan: personal data, secrets and injection payloads, before a pack travels.
    private static int ScanPack(Options o)
    {
        var target = o.Positional(0, "folder or file");
        var content = PackReader.Read(target);
        if (o.Value("policy") is not null && PackVerifier.VerifyWhole(content, Policy(o), DateTimeOffset.UtcNow) is { IsVerified: false } refused)
            return Report(refused.Problems, "");
        // The bytes read once, verified when a policy is given, are the bytes scanned (review 7, D2).
        AgentMemory.Extensibility.Ampack.PackScanReport report;
        try { report = AgentMemory.Extensibility.Ampack.PackScan.Scan(content.Files, verified: o.Value("policy") is not null); }
        catch (AgentMemory.Extensibility.Ampack.PackLoadException e) { return Report(e.Problems, ""); }
        foreach (var finding in report.Findings)
            Console.WriteLine(Shown($"  {finding.Severity,-6} {finding.Rule,-34} {finding.File}{(finding.Line > 0 ? ":" + finding.Line : "")} {finding.Path}  {finding.Sample}", 2000));
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
        var content = PackReader.ReadArchive(PackReader.ReadArchiveFile(target), target);
        if (Unverified(content, PackVerifier.Before(Policy(o), role), target) is { Count: > 0 } problems) return Report(problems, "");
        var files = content.Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        PackBuilder.Sign(files, signer, role, SigningTime());
        File.WriteAllBytes(o.Required("out"), PackBuilder.Pack(files));
        Console.WriteLine($"Countersigned as {role}: {o.Required("out")}");
        return 0;
    }

    // Why a pack another command acts on does not verify under the host's policy, each problem named with its file; empty when it
    // does (review 7, L7: countersign, lock and set acted on a pack whose signatures nobody checked, so an assurer could vouch for,
    // a lock pin and a set carry what no host would load).
    private static IReadOnlyList<string> Unverified(PackContent content, TrustPolicy policy, string path) =>
        [.. PackVerifier.VerifyWhole(content, policy, DateTimeOffset.UtcNow).Problems.Select(p => $"{path}: {p}")];

    private static int Sign(Options o)
    {
        var folder = o.Positional(0, "folder");
        var files = PackReader.ReadFolder(folder).Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var integrity = PackVerifier.CheckIntegrity(new PackContent(files, folder));
        if (integrity.Problems.Count > 0) return Report(integrity.Problems, "");
        using var signer = Signer(o);
        var path = PackBuilder.Sign(files, signer, o.Value("role") ?? "publisher", SigningTime());
        // The writer's read-back on the folder too (review 8, F9): what is written is what every reader accepts.
        if (PackVerifier.CheckIntegrity(new PackContent(files, folder)).Problems is { Count: > 0 } refused) return Report(refused, "");
        WriteMeta(folder, files);
        Console.WriteLine($"Signed {integrity.List!.Id} {integrity.List.Version}: {path}");
        return 0;
    }

    // A pack's profiles as verify, inspect and the kit show them: names in the registry's order, or "core" for none.
    private static string Profiles(VerifiedPack pack)
    {
        var used = PackProfiles.Used(pack.Files.Keys, pack.Manifest);
        if (used.Count == 0) return "core";
        // R-CORE-003: a reader says which profile it does not implement (feature 01 review, F01-4).
        var missing = used.Where(u => !u.Implemented).Select(u => u.Name).ToList();
        return string.Join(", ", used.Select(u => u.Name)) + (missing.Count > 0 ? $" (not implemented by this reader: {string.Join(", ", missing)})" : "");
    }

    private static int Verify(Options o)
    {
        var target = o.Positional(0, "folder or file");
        var ledgerPath = o.Value("ledger");
        if (o.Flag("record") && ledgerPath is null) throw new UsageException("--record writes the ledger: give --ledger <ledger.json>.");
        var policy = Policy(o);
        var ledger = ledgerPath is null ? null : HostLedger.Load(ledgerPath);
        var content = PackReader.Read(target);
        Action<PackStep>? explain = o.Flag("explain") ? Explain : null;
        var result = PackVerifier.VerifyOne(content, policy, DateTimeOffset.UtcNow, ledger, o.Flag("allow-downgrade"), explain);
        if (!result.IsVerified) return Report(result.Problems, "");
        var pack = result.Pack!;
        if (pack.Manifest.Kind == "set")
        {
            // A set verifies as a whole and member by member: each keeps its own list and signatures.
            // The set as a whole was just verified: passed on, not done again (review 4).
            var (whole, members) = PackVerifier.VerifySet(content, policy, DateTimeOffset.UtcNow, ledger, o.Flag("allow-downgrade"), verified: result);
            // The set as a whole first: a file outside any member folder, or members other than it names, refuses it.
            if (!whole.IsVerified) return Report(whole.Problems, "");
            var refused = members.Where(m => !m.Value.IsVerified).SelectMany(m => m.Value.Problems.Select(p => $"packs/{m.Key}/: {p}")).ToList();
            if (refused.Count > 0) return Report(refused, "");
            Console.WriteLine($"Verified set {pack.Id} {pack.Version}\n  digest  {pack.Digest}");
            Console.WriteLine($"  profiles {Profiles(pack)}");
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
        // The profiles it uses, by the registry's map (feature 01): a host must implement each to load it.
        Console.WriteLine($"  profiles {Profiles(pack)}");
        // Each set of vectors, as its index states it (feature 03): what a host's embedder must match to reuse them.
        foreach (var set in PackEmbeddings.Read(pack.Files).Sets)
            Console.WriteLine($"  vectors {set.Name}: {set.Rows.Count} rows of {set.Dimension} {set.DataType}, {Shown(set.Model, 200)}"
                + (set.ModelDigest is { } digest ? $" {Shown(digest, 200)}" : "") + $", {set.Distance}{(set.Normalized ? ", normalized" : "")}");
        foreach (var s in pack.Signatures) Console.WriteLine($"  signed  {s.Role} by {Dsse.Short(s.KeyId)} at {s.SignedAt:u}");
        // What a trial decision (and a certificate issued from one) binds besides the digest: the declarations it ran.
        if (pack.Manifest.Kind is "knowledge" or "definition")
            try { Console.WriteLine($"  declarations {AgentMemory.Extensibility.Modules.PackTrialGate.DigestOf(AgentMemory.Extensibility.Modules.ModuleManifest.Parse(Encoding.UTF8.GetString(PackLayout.ToModule(pack.Files).Manifest)))}"); }
            catch (Exception e) when (e is InvalidDataException or FormatException) { }
        if (pack.Manifest.Federation is { } federation)
            Console.WriteLine($"  publisher {federation.Publisher}\n  for     {PackFederation.Describe(federation.Audience)}" + (federation.Purpose is { } why ? $"\n  purpose {Shown(why, 300)}" : ""));
        ShowCardAndCertificates(pack, ledger?.Apply(policy) ?? policy);
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
            var manifest = content.Files.TryGetValue(PackManifest.FileName, out var manifestBytes) ? PackManifest.Parse(manifestBytes).Manifest : null;
            if (manifest is { FormatMinor: > PackManifest.Minor })
                Console.WriteLine($"Format  agentmemory.pack/1.{manifest.FormatMinor}, newer than this tool's {PackManifest.Format}: read as far as it knows it" +
                    (manifest.IgnoredFields.Count > 0 ? $"; ignored {string.Join(", ", manifest.IgnoredFields.Select(f => Shown(f, 80)))}" : ""));
            if (manifest?.Federation is { } federation)
                Console.WriteLine($"Federated: publisher {federation.Publisher}, for {PackFederation.Describe(federation.Audience)}  (not verified)");
            if (content.Files.TryGetValue(PackCard.FileName, out var cardBytes) && PackCard.Parse(cardBytes).Card is { } card)
            {
                Console.WriteLine($"Card    {Shown(card.OwnerName, 200)}: {Shown(card.IntendedUse, 200)}  (not verified)");
                // What every turn costs (feature 06): the text the model is shown, and the bytes a trial counts, framed as the
                // model reads it (review F06, L2: the count before the frame differed from the trial's).
                if (card.WhenToUse is { } whenToUse)
                {
                    var framed = AgentMemory.Extensibility.Modules.PackTrialRender.AlwaysShown(list.Id, list.Version, whenToUse);
                    Console.WriteLine($"When    {Shown(whenToUse, 1000)}  ({System.Text.Encoding.UTF8.GetByteCount(framed):N0} bytes a turn, framed)  (not verified)");
                }
            }
            // Each set of vectors as its index states it (feature 03; review F03: inspect showed none).
            foreach (var set in PackEmbeddings.Read(content.Files).Sets)
                Console.WriteLine($"Vectors {set.Name}: {set.Rows.Count} rows of {set.Dimension} {set.DataType}, {Shown(set.Model, 200)}, {set.Distance}  (not verified)");
            foreach (var f in list.Files) Console.WriteLine($"  {f.Length,10:N0}  {f.Path}");
        }
        else Console.WriteLine("Not built: no valid META/files.json.");
        foreach (var path in content.Files.Keys.Where(PackPaths.IsSignature).Order(StringComparer.Ordinal))
            Console.WriteLine($"  {(PackPaths.IsTimestamp(path) ? "timestamp" : "envelope")}  {Shown(path, 300)}");
        foreach (var (stem, _) in PackAttestations.Certificates(content.Files))
            Console.WriteLine($"  certificate  {PackAttestations.CertificatePath(stem)}{(PackAttestations.Run(content.Files, stem) is { } run ? $" (with an AEF run of {run.Count} files)" : "")}");
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
        var passphraseVariable = o.Value("passphrase-env");
        if (passphraseVariable is not null && protect is not null) throw new UsageException("--passphrase-env and --protect are two ways to protect the key: choose one.");
        var passphrase = passphraseVariable is null ? null : Environment.GetEnvironmentVariable(passphraseVariable) is { Length: >= 12 } secret ? secret
            : throw new UsageException($"--passphrase-env names {passphraseVariable}, which must hold a passphrase of at least 12 characters.");
        if (protect == "dpapi" && !OperatingSystem.IsWindows()) throw new UsageException("--protect dpapi is Windows only.");
        var privatePath = Path.Combine(folder, name + (protect == "dpapi" ? ".key.dpapi" : ".key.pem"));
        if (File.Exists(privatePath)) throw new UsageException($"{privatePath} exists; refusing to overwrite a key.");
        if (protect == "dpapi" && OperatingSystem.IsWindows())
            File.WriteAllBytes(privatePath, System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(key.PrivateKeyPem), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
        else WritePrivate(privatePath, passphrase is null ? key.PrivateKeyPem : EncryptedPem(key.PrivateKeyPem, passphrase));
        File.WriteAllText(Path.Combine(folder, name + ".pub.pem"), key.PublicKeyPem);
        var entry = new JsonObject
        {
            ["keyId"] = key.KeyId, ["publicKey"] = key.PublicKeyPem,
            ["idPrefixes"] = new JsonArray((JsonNode)(o.Value("prefix") ?? "io.example")), ["roles"] = new JsonArray((JsonNode)role),
        };
        if (o.Value("prefix") is null) Console.WriteLine("Prefix  io.example (the default: pass --prefix with your own namespace before this key signs anything real)");
        Console.WriteLine($"Key id  {key.KeyId}\nPrivate {privatePath}  (keep it secret; never commit it)\nTrust-policy entry:\n{Pretty(entry)}");
        // --policy: the entry added to a trust policy file (created when missing), which is then read back as a host reads it.
        if (o.Value("policy") is { } policyPath)
        {
            var policy = File.Exists(policyPath) ? JsonNode.Parse(Text(policyPath, "The trust policy", MaximumPolicyBytes))?.AsObject() ?? throw new UsageException($"{policyPath} is not a JSON object.")
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
            return PackSigner.FromCommand(command, Text(o.Value("public") ?? throw new UsageException("--signer needs --public <pub.pem>, the key it signs with."), "The public key"));
        return PackSigner.FromPem(PrivatePem(o.Required("key")));
    }

    // A private key as PEM, from a file, env:<VARIABLE> or dpapi:<file>; an encrypted PKCS#8 key (keygen --passphrase-env) is
    // opened with the passphrase in AMPACK_KEY_PASSPHRASE. Every command that signs reads its key here (review 1, M12 and L8).
    private static string PrivatePem(string key)
    {
        string pem;
        if (key.StartsWith("env:", StringComparison.Ordinal))
            pem = Environment.GetEnvironmentVariable(key[4..]) is { Length: > 0 } value ? value : throw new UsageException($"The environment variable {key[4..]} holds no key.");
        else if (key.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) throw new UsageException("dpapi: keys are Windows only; elsewhere use an encrypted key (keygen --passphrase-env) or --signer.");
            pem = Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(PackReader.ReadFile(key[6..], MaximumKeyBytes, "The key"), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        else pem = Text(key, "The key");
        if (!pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal)) return pem;
        var passphrase = Environment.GetEnvironmentVariable("AMPACK_KEY_PASSPHRASE");
        if (string.IsNullOrEmpty(passphrase)) throw new UsageException("The key is encrypted: set AMPACK_KEY_PASSPHRASE to its passphrase.");
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        try { ecdsa.ImportFromEncryptedPem(pem, passphrase); }
        catch (System.Security.Cryptography.CryptographicException) { throw new UsageException("The key does not open with the passphrase in AMPACK_KEY_PASSPHRASE."); }
        return ecdsa.ExportPkcs8PrivateKeyPem();
    }

    // A private key file only its owner can read (on Linux and macOS 0600; on Windows the folder's ACL applies), created new.
    private static void WritePrivate(string path, string pem)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(Encoding.ASCII.GetBytes(pem));
    }

    // An encrypted PKCS#8 PEM: AES-256-CBC, key from PBKDF2-SHA256 with 600,000 iterations.
    private static string EncryptedPem(string pem, string passphrase)
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return ecdsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase, new System.Security.Cryptography.PbeParameters(
            System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc, System.Security.Cryptography.HashAlgorithmName.SHA256, 600_000));
    }

    // The reference signer for --signer: what a keychain, KMS or HSM command must do.
    // Publication receipts (federated packs §5): one placement of a federated pack at a URL, signed apart from the pack.
    private static int Receipt(Options o)
    {
        var action = o.Positional(0, "receipt action (sign or verify)");
        var archivePath = o.Positional(1, "pack.ampack");
        var location = o.Required("location");
        var archive = PackReader.ReadArchiveFile(archivePath);
        var content = PackReader.ReadArchive(archive, archivePath);
        if (action == "verify")
        {
            var policy = Policy(o);
            // A set is verified with every member it carries, never on its outer signature alone (FED-review H2).
            var verified = PackVerifier.VerifyWhole(content, policy, DateTimeOffset.UtcNow);
            if (!verified.IsVerified) return Report(verified.Problems, "");
            var (receipt, _, problems) = PublicationReceipt.Verify(PackReader.ReadFile(o.Positional(2, "receipt file"), Dsse.MaximumEnvelopeBytes, "The receipt"), verified.Pack!, archive, location, policy, DateTimeOffset.UtcNow);
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
        using var signer = Signer(o);
        // Only a key that signed this pack as its publisher places it (review 1, L13); a host also checks the WebID binding.
        // Review 2: a signature by that key that verifies, as publisher, not only a file of that name.
        var envelopeName = Dsse.PathFor("publisher", signer.KeyId);
        var onlyThisKey = new TrustPolicy { Keys = [new TrustedKey(signer.KeyId, signer.PublicKeyPem, [list!.Id], ["publisher"], null, null)] };
        if (!content.Files.TryGetValue(envelopeName, out var envelopeBytes)
            || !Dsse.Verify(envelopeName, envelopeBytes, FilesList.Hex(listBytes!), list, onlyThisKey, DateTimeOffset.UtcNow).Valid.Any(v => v.Role == "publisher"))
            return Report([$"Key {Dsse.Short(signer.KeyId)} did not sign this pack as its publisher: a receipt is signed with the publisher's key."], "");
        var receiptValue = new PublicationReceipt(list!.Id, list.Version, digest, location, FilesList.Hex(archive), audience, access,
            List(o.Value("denied")), List(o.Value("allowed")), DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var bytes = receiptValue.Sign(signer);
        var outPath = o.Required("out");
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"Receipt {outPath}\n  pack    {list.Id} {list.Version} {digest}\n  at      {location}\n  for     {PackFederation.Describe(audience)} ({access})\n  signed  by {Dsse.Short(signer.KeyId)}");
        return 0;
    }

    private static int SignBytes(Options o)
    {
        // Read bounded, as every other input (the choke-point ratchet found this one reading whole): what an external signer
        // signs is a statement or a list, never larger than a file a pack may hold.
        using var input = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        for (int read; (read = input.Read(chunk)) > 0;)
        {
            if (buffer.Length + read > PackLimits.Default.MaxFileBytes)
                throw new UsageException($"The bytes to sign are larger than {PackLimits.Default.MaxFileBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(PrivatePem(o.Required("key")));
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
        CheckLedger(o);
        var policy = TrustPolicy.Parse(PackReader.ReadFile(o.Required("policy"), MaximumPolicyBytes, "The trust policy"));
        var ledgerPath = LedgerPath(o);
        var ledger = ledgerPath is null ? null : HostLedger.Load(ledgerPath);
        var documents = o.Values("revocations").Select(r => PackReader.ReadArchiveFile(r, Revocations.DocumentLimits)).ToList();
        policy = Revocations.ApplyAll(policy, documents, DateTimeOffset.UtcNow, ledger, out var notApplied);
        foreach (var note in notApplied) Console.Error.WriteLine("Note: " + Shown(note, 2000));
        if (ledger is not null && documents.Count > 0) ledger.Save(ledgerPath!);
        return policy;
    }

    // The same, as the loader's trust.
    private static AgentMemory.Extensibility.Ampack.PackTrust Trust(Options o)
    {
        CheckLedger(o);
        var trust = AgentMemory.Extensibility.Ampack.PackTrust.Load(o.Required("policy"));
        var ledger = LedgerPath(o) is { } path ? AgentMemory.Extensibility.Ampack.PackInstallLedger.Open(path) : null;
        var documents = o.Values("revocations").Select(r => PackReader.ReadArchiveFile(r, Revocations.DocumentLimits)).ToList();
        trust = trust.WithRevocations(documents, ledger, out var notApplied);
        foreach (var note in notApplied) Console.Error.WriteLine("Note: " + Shown(note, 2000));
        return trust;
    }

    // A ledger named with --ledger is there, unless this run starts it with --record: a mistyped path was read as an empty
    // ledger, and a rolled-back pack verified (review 2). Revocation lists do not start one (review 3), and a --store is there
    // before anything reads its ledger (review 4: serve, test and ledger made a mistyped store's ledger.json first); install
    // and solid fetch open the store they fill before they get here.
    private static void CheckLedger(Options o)
    {
        if (o.Value("ledger") is { } path && !File.Exists(path) && !o.Flag("record"))
            throw new UsageException($"There is no ledger at {path}. Give the right path, or --record to start one.");
        if (o.Value("store") is { } store) ExistingStore(store);
    }

    // A host that stops using revocation lists forgets the newest list's freshness; what it revoked stays revoked.
    private static int Ledger(Options o)
    {
        var path = LedgerPath(o) ?? throw new UsageException("ledger needs --ledger <ledger.json> or --store <folder>.");
        CheckLedger(o);
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
        var policy = Policy(o);
        foreach (var memberPath in o.PositionalFrom(1).Order(StringComparer.Ordinal))
        {
            var content = PackReader.Read(memberPath);
            if (Unverified(content, policy, memberPath) is { Count: > 0 } refused) return Report(refused, "");
            var integrity = PackVerifier.CheckIntegrity(content);
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
        var policy = Policy(o);
        foreach (var path in o.PositionalFrom(1))
        {
            var content = PackReader.Read(path);
            if (Unverified(content, policy, path) is { Count: > 0 } refused) return Report(refused, "");
            var integrity = PackVerifier.CheckIntegrity(content);
            if (!content.Files.Keys.Any(PackPaths.IsEnvelope)) return Report([$"{path}: a set carries signed members only."], "");
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
        var gate = ServeGate(o);
        try
        {
            // The host's consent grants (review 5: serve took none, so a relation a host walks was refused here).
            var grants = o.Values("grant").Select(g => g.Split('=') is [var module, var into] && module.Length > 0 && into.Length > 0
                ? new AgentMemory.Extensibility.Modules.ModuleConsentGrant(module, into)
                : throw new UsageException($"--grant {g}: give <module>=<into>, the module whose relation reaches into the other's records.")).ToList();
            var mount = AgentMemory.Extensibility.Ampack.PackMemory.Mount(archives, trust, gate, grants);
            // The export door (feature 10, R-CORE-139): serving hands a pack's records to another program, whose model is of the
            // class the operator states (--client-endpoint; unstated, external). Asked here and on every request.
            PackTools.ClientEndpoint = o.Value("client-endpoint") is { } endpoint
                ? endpoint is "local" or "private" or "external" ? endpoint : throw new UsageException($"--client-endpoint {endpoint}: give local, private or external.")
                : "external";
            var kept = mount.Packs.Select(p => PackTools.Off(p, DateTimeOffset.UtcNow)).OfType<string>().Where(off => off.Contains("usage policy", StringComparison.Ordinal)).ToList();
            if (kept.Count != 0) return Report(kept, "");
            // --print-config (feature 14): what an MCP client needs to start this same server, once the packs verify.
            if (o.Flag("print-config")) return PrintClientConfig(o);
            // Review 1, I9: a pack that declares a trial, served with no gate, is said to be served ungated.
            if (gate is null)
                foreach (var pack in mount.Packs.Where(p => p.Manifest.Trial is not null))
                    Console.Error.WriteLine($"ampack serve: {pack.Id} declares a trial but is served ungated; give --activations and --host-key to apply the host's decisions.");
            return o.Value("http") is { } url ? Rest.Run(mount, url, o.Flag("allow-remote")) : Serve.Run(mount);
        }
        catch (AgentMemory.Extensibility.Ampack.PackLoadException e) { return Report(e.Problems, ""); }
    }

    // The MCP client configuration for this server (feature 14, R4): this tool, the packs and every option given but this one,
    // with full paths, for Claude Desktop (claude_desktop_config.json), VS Code (.vscode/mcp.json) and Cursor (~/.cursor/mcp.json).
    private static int PrintClientConfig(Options o)
    {
        var args = new List<string> { "serve" };
        args.AddRange(o.PositionalFrom(0).Select(Path.GetFullPath));
        foreach (var name in (string[])["policy", "store", "activations", "host-key", "client-endpoint"])
            if (o.Value(name) is { } value) args.AddRange(["--" + name, name is "client-endpoint" ? value : Path.GetFullPath(value)]);
        foreach (var value in o.Values("revocations")) args.AddRange(["--revocations", Path.GetFullPath(value)]);
        foreach (var value in o.Values("grant")) args.AddRange(["--grant", value]);
        var command = Environment.ProcessPath ?? "ampack";
        var server = new System.Text.Json.Nodes.JsonObject
        {
            ["command"] = command, ["args"] = new System.Text.Json.Nodes.JsonArray([.. args.Select(a => (System.Text.Json.Nodes.JsonNode)a)]),
        };
        var indented = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        string Of(string root, System.Text.Json.Nodes.JsonObject entry) =>
            new System.Text.Json.Nodes.JsonObject { [root] = new System.Text.Json.Nodes.JsonObject { ["ampack"] = entry } }.ToJsonString(indented);
        Console.WriteLine("Claude Desktop (claude_desktop_config.json):");
        Console.WriteLine(Of("mcpServers", (System.Text.Json.Nodes.JsonObject)server.DeepClone()));
        Console.WriteLine("VS Code (.vscode/mcp.json):");
        var vscode = (System.Text.Json.Nodes.JsonObject)server.DeepClone();
        vscode["type"] = "stdio";
        Console.WriteLine(Of("servers", vscode));
        Console.WriteLine("Cursor (~/.cursor/mcp.json):");
        Console.WriteLine(Of("mcpServers", (System.Text.Json.Nodes.JsonObject)server.DeepClone()));
        return 0;
    }

    // --activations with --host-key: the host's trial gate decides what is served, as it decides what composes (review 1, M1).
    private static Func<IReadOnlyList<AgentMemory.Extensibility.Ampack.LoadedPack>, IReadOnlySet<string>, IReadOnlyCollection<string>>? ServeGate(Options o)
    {
        if (o.Value("activations") is not { } folder)
        {
            // Gate options without the host's decisions would serve with no gate, silently (review 9, B-M1).
            if (GateOptions.Where(n => n is not ("activations" or "host-key")).FirstOrDefault(n => o.Value(n) is not null) is { } alone)
                throw new UsageException($"--{alone} applies the host's trial gate, so it needs the host's decisions: give --activations and --host-key.");
            return o.Value("host-key") is null ? null : throw new UsageException("--host-key goes with --activations.");
        }
        var activations = new AgentMemory.Extensibility.Modules.PackActivationFolder(folder, Text(o.Required("host-key"), "The host key"));
        GateCheck(o, [], activations);
        return (packs, familyMembers) =>
        {
            var check = GateCheck(o, [.. packs.Where(p => p.Kind != "snapshot")], activations, familyMembers);
            foreach (var off in check.Off)
            {
                Console.Error.WriteLine($"ampack serve: {off.ModuleId} {off.Version} stays off: {Shown(off.Reason, 300)}");
                PackTools.KeptOff[off.ModuleId] = $"{off.ModuleId} {off.Version} is kept off by the host's trial gate: {Shown(off.Reason, 300)}";
            }
            return [.. check.Off.Select(off => off.ModuleId)];
        };
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
                    Console.WriteLine($"  {(result.Passed ? "PASS" : "FAIL")}  {pack.Id}: {Shown(result.Name)}: {Shown(result.Detail, 1000)}");
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
            foreach (var stale in Directory.EnumerateFiles(signatures, "*", SearchOption.AllDirectories))
                if (!files.ContainsKey("META/signatures/" + Path.GetRelativePath(signatures, stale).Replace(Path.DirectorySeparatorChar, '/'))) File.Delete(stale);
        // Certificates and their runs too, as the archive drops them (review 2: the folder kept them for the old digest).
        var attestations = Path.Combine(folder, "META", "attestations");
        if (Directory.Exists(attestations))
            foreach (var stale in Directory.EnumerateFiles(attestations, "*", SearchOption.AllDirectories))
                if (!files.ContainsKey("META/attestations/" + Path.GetRelativePath(attestations, stale).Replace(Path.DirectorySeparatorChar, '/'))) File.Delete(stale);
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
        if (o.Value("store") is { } store) archives.AddRange(AgentMemory.Extensibility.Ampack.PackStore.Open(ExistingStore(store)).Archives());
        return archives.Count > 0 ? archives : throw new UsageException($"{command} needs at least one pack, or --store with packs installed.");
    }

    // A store a command only reads is there already (review 3: a mistyped --store was created empty, its ledger with it, and
    // what the host had revoked was forgotten). install and solid fetch make one.
    private static string ExistingStore(string folder) =>
        Directory.Exists(Path.Combine(folder, "packs")) ? folder : throw new UsageException($"There is no pack store at {folder}: install a pack there first.");

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
        var store = AgentMemory.Extensibility.Ampack.PackStore.Open(ExistingStore(o.Required("store")));
        if (!store.Uninstall(id)) return Report([$"{id} is not installed."], "");
        Console.WriteLine($"Removed {id}; the store's ledger keeps it.");
        return 0;
    }

    private static int ListInstalled(Options o)
    {
        var store = AgentMemory.Extensibility.Ampack.PackStore.Open(ExistingStore(o.Required("store")));
        if (store.Installed.Count == 0) Console.WriteLine("No packs installed.");
        foreach (var p in store.Installed) Console.WriteLine($"  {p.Id} {p.Version} ({p.Kind})  {p.Digest[..23]}…  installed {p.InstalledAt:u}");
        return 0;
    }

    // PACK-toolset: ampack diff.
    private static int DiffPacks(Options o)
    {
        var (first, second) = (o.Positional(0, "first pack"), o.Positional(1, "second pack"));
        // Each pack read once: what is verified is what is compared (review 7, D2: verified, then read again by path).
        var (a, b) = (PackReader.Read(first), PackReader.Read(second));
        var verified = o.Value("policy") is not null;
        if (verified)
        {
            var policy = Policy(o);
            foreach (var (path, content) in new[] { (first, a), (second, b) })
                if (PackVerifier.VerifyWhole(content, policy, DateTimeOffset.UtcNow) is { IsVerified: false } refused)
                    return Report(refused.Problems.Select(p => $"{path}: {p}"), "");
        }
        AgentMemory.Extensibility.Ampack.PackDiff diff;
        try { diff = AgentMemory.Extensibility.Ampack.PackDiff.Compare(a.Files, b.Files, verified); }
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
        // Everything below quotes the packs: shown, never trusted (review 1, M11).
        static void Say(string line) => Console.WriteLine(Shown(line, 2000));
        Say($"{diff.Before.Id} {diff.Before.Version} -> {diff.After.Id} {diff.After.Version}{(diff.Same ? "  (the same pack)" : "")}");
        Say($"  digest  {diff.Before.Digest} -> {diff.After.Digest}");
        foreach (var group in diff.Declarations.GroupBy(d => d.File))
        {
            Say($"  {group.Key}");
            foreach (var change in group)
                Say($"    {Mark(change.Change)} {change.Path}: " + change.Change switch
                {
                    AgentMemory.Extensibility.Ampack.PackChange.Added => Short(change.After!),
                    AgentMemory.Extensibility.Ampack.PackChange.Removed => Short(change.Before!),
                    _ => $"{Short(change.Before!)} -> {Short(change.After!)}",
                });
        }
        var counts = diff.Records.GroupBy(r => r.Change).ToDictionary(g => g.Key, g => g.Count());
        Say($"  records  +{counts.GetValueOrDefault(AgentMemory.Extensibility.Ampack.PackChange.Added)} " +
            $"-{counts.GetValueOrDefault(AgentMemory.Extensibility.Ampack.PackChange.Removed)} ~{counts.GetValueOrDefault(AgentMemory.Extensibility.Ampack.PackChange.Changed)}, {diff.UnchangedRecords} unchanged");
        foreach (var record in diff.Records.Take(50))
            Say($"    {Mark(record.Change)} {record.Type} '{record.Key}'" + (record.Change == AgentMemory.Extensibility.Ampack.PackChange.Changed
                ? ": " + string.Join(", ", record.Properties) + (record.LinksChanged ? (record.Properties.Count > 0 ? ", links" : "links") : "") : ""));
        if (diff.Records.Count > 50) Say($"    ... and {diff.Records.Count - 50} more (--json lists all)");
        if (diff.Files.Count > 0)
        {
            Say("  files");
            foreach (var file in diff.Files) Say($"    {Mark(file.Change)} {file.Path} ({file.Before?.ToString("N0") ?? "-"} -> {file.After?.ToString("N0") ?? "-"} bytes)");
        }
        return 0;
    }

    private static string Short(string json) => json.Length <= 80 ? json : json[..77] + "...";

    // Every file a command names is read bounded, never through a link (review 5, L1: six commands read whole, `ampack lint
    // /dev/zero` about 2 GB): packs at the archive limit, these at theirs.
    private const int MaximumPolicyBytes = TrustPolicy.MaximumBytes, MaximumKeyBytes = 64 * 1024;

    private static string Text(string path, string what, int maximum = MaximumKeyBytes) => Encoding.UTF8.GetString(PackReader.ReadFile(path, maximum, what));

    // verify --explain: one line per step, then its details.
    private static void Explain(PackStep step)
    {
        // Step lines quote the pack (paths, field names, the purpose): shown, never trusted (review 1, M11).
        Console.WriteLine($"{(step.Passed ? "  ok  " : "  FAIL")}  {Shown(step.Name, 200)}");
        foreach (var line in step.Lines) Console.WriteLine($"          {Shown(line, 2000)}");
    }

    private static int Report(IEnumerable<string> problems, string success)
    {
        var list = problems.ToList();
        if (list.Count == 0) { Console.WriteLine(success); return 0; }
        foreach (var p in list) Console.Error.WriteLine("refused: " + Shown(p, 4000));
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
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "stored", "safety", "record", "allow-downgrade", "explain", "json", "p1363", "allow-remote", "print-config", "retire-revocations", "allow-loopback-http", "no-recheck", "git", "help" };
    // The options that take several values, each given on its own.
    private static readonly HashSet<string> Repeatable = new(StringComparer.Ordinal) { "revocations" };
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
            // Review 2: a value is never the next option (--ledger --record ledger.json read "--record" as the ledger path), and
            // an option given twice is a mistake unless it takes several values.
            if (list[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"--{name} needs a value; {list[i + 1]} is an option.");
            if (!options._values.TryGetValue(name, out var values)) options._values[name] = values = [];
            else if (!Repeatable.Contains(name)) throw new UsageException($"--{name} is given twice; it takes one value.");
            values.Add(list[++i]);
        }
        return options;
    }

    internal string Positional(int index, string what) =>
        index < _positional.Count ? _positional[index] : throw new UsageException($"Missing the {what}.");

    internal IEnumerable<string> PositionalFrom(int index) => _positional.Skip(index);

    /// <summary>How many arguments that are not options were given.</summary>
    internal int PositionalCount => _positional.Count;
    internal string? Value(string name) => _values.TryGetValue(name, out var values) ? values[^1] : null;
    /// <summary>Every value an option was given, in order (an option given more than once, such as --revocations).</summary>
    internal IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var values) ? values : [];
    internal string Required(string name) => Value(name) ?? throw new UsageException($"Missing --{name}.");
    internal bool Flag(string name) => _flags.Contains(name);
    /// <summary>Every option and flag given, by name.</summary>
    internal IEnumerable<string> Names => _values.Keys.Concat(_flags);
}
