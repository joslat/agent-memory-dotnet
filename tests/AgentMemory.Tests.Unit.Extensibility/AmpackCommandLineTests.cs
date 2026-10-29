using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Coding;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The <c>ampack</c> tool end to end (PACK-tool-e2e): every command run as a process, as an author runs it, on a new pack and
/// on the real packs; the host then loads what the tool built. Exit codes are the tool's contract: 0 done, 1 refused, 2 usage.
/// </summary>
public sealed class AmpackCommandLineTests : IDisposable
{
    private static readonly string Packs = Path.Combine(AppContext.BaseDirectory, "Packs");
    private static readonly string Tool = Locate();
    private readonly string _root = Directory.CreateTempSubdirectory("ampack-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Locate()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AgentMemory.slnx")))
            root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
        var configuration = typeof(AmpackCommandLineTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        return Path.Combine(root.FullName, "tools", "Ampack", "bin", configuration, "net10.0", "Ampack.dll");
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private (int Exit, string Output) Ampack(params string[] args) => Ampack(null, args);

    private (int Exit, string Output) Ampack(IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root,
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        start.ArgumentList.Add(Tool);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ampack " + string.Join(' ', args) + " did not finish in 60 s.");
        }
        return (process.ExitCode, output.Result + error.Result);
    }

    private string Done(params string[] args)
    {
        var (exit, output) = Ampack(args);
        exit.Should().Be(0, "ampack " + string.Join(' ', args) + " printed: " + output);
        return output;
    }

    private string Refused(params string[] args)
    {
        var (exit, output) = Ampack(args);
        exit.Should().Be(1, "ampack " + string.Join(' ', args) + " printed: " + output);
        return output;
    }

    [Fact]
    public void Every_command_works_on_a_new_pack_and_refuses_a_downgrade_and_a_changed_record()
    {
        // keygen: a key pair and the trust-policy entry for it, made into the host's trust policy.
        var key = Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.example");
        File.Exists(At("keys", "publisher.key.pem")).Should().BeTrue();
        var entry = JsonNode.Parse(key[(key.IndexOf("Trust-policy entry:", StringComparison.Ordinal) + "Trust-policy entry:".Length)..])!.AsObject();
        entry["roles"] = new JsonArray("publisher", "curator");
        File.WriteAllText(At("trust.json"), new JsonObject { ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = new JsonArray(entry) }.ToJsonString());

        // init, lint, build with a signature, verify with the ledger, inspect.
        Done("init", "notes", "--id", "io.example.notes");
        Directory.CreateDirectory(At("notes", "evals"));
        File.WriteAllText(At("notes", "evals", "tests.json"),
            """{"format":"agentmemory.pack.tests/1.2","tests":[{"name":"the note","view":"notes","search":"first note","expect":["note-1"]}]}""");
        Done("lint", "notes");
        Done("build", "notes", "--out", "notes.ampack", "--key", At("keys", "publisher.key.pem")).Should().Contain("sha256:");
        Done("verify", "notes.ampack", "--policy", "trust.json", "--ledger", "ledger.json", "--record")
            .Should().Contain("Verified io.example.notes 0.1.0").And.Contain("kind    knowledge").And.Contain("ledger  recorded");
        Done("inspect", "notes.ampack").Should().Contain("io.example.notes 0.1.0").And.Contain("envelope  META/signatures/");
        // test: loaded as a host loads it, held in memory, its own tests run.
        Done("test", "notes.ampack", "--policy", "trust.json").Should().Contain("PASS  io.example.notes: the note").And.Contain("1 of 1 passed");

        // sign: a second signature in another role; both verify.
        Done("sign", "notes", "--key", At("keys", "publisher.key.pem"), "--role", "curator");
        Done("build", "notes", "--out", "curated.ampack");
        Done("verify", "curated.ampack", "--policy", "trust.json").Should().Contain("signed  curator").And.Contain("signed  publisher");

        // An older version is refused by the ledger; a record changed after the build is refused by the files list.
        var pack = JsonNode.Parse(File.ReadAllText(At("notes", "pack.json")))!.AsObject();
        pack["version"] = "0.0.9";
        File.WriteAllText(At("notes", "pack.json"), pack.ToJsonString());
        Done("build", "notes", "--out", "older.ampack", "--key", At("keys", "publisher.key.pem"));
        Refused("verify", "older.ampack", "--policy", "trust.json", "--ledger", "ledger.json").Should().Contain("older than 0.1.0");
        var record = Directory.EnumerateFiles(At("notes", "records")).First();
        File.WriteAllText(record, File.ReadAllText(record).Replace("\"", "'", StringComparison.Ordinal));
        Refused("verify", "notes", "--policy", "trust.json").Should().Contain("does not match META/files.json");

        // A definition pack: declarations only, no records.
        Done("init", "defs", "--id", "io.example.defs", "--kind", "definition");
        Directory.Exists(At("defs", "records")).Should().BeFalse();
        Done("build", "defs", "--out", "defs.ampack", "--key", At("keys", "publisher.key.pem"));
        Done("verify", "defs.ampack", "--policy", "trust.json").Should().Contain("kind    definition");

        // Usage errors: an unknown command, a missing id, a kind init does not make.
        Ampack("init", "other", "--id", "io.example.other", "--kind", "composite").Exit.Should().Be(2);
        Ampack("frobnicate").Exit.Should().Be(2);
        Ampack("init", "other").Exit.Should().Be(2);
    }

    [Fact]
    public void The_real_packs_migrate_build_lock_and_ship_as_one_set_that_the_host_loads()
    {
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.agentmemory");
        var keyPath = At("keys", "publisher.key.pem");
        var publicKey = File.ReadAllText(At("keys", "publisher.pub.pem"));
        File.WriteAllText(At("trust.json"), PackAuthoring.TrustPolicy(publicKey, "io.agentmemory", "io.example"));

        // A knowledge pack and the four Coding definitions, migrated from their bundles, built, signed and verified.
        Done("migrate", Path.Combine(Packs, "MartialArts"), "--out", "martialarts");
        Done("build", "martialarts", "--out", "martialarts.ampack", "--key", keyPath);
        Done("verify", "martialarts.ampack", "--policy", "trust.json").Should().Contain("kind    knowledge");
        var members = new List<string>();
        foreach (var folder in new[] { "Decisions", "Repository", "CodeMap", "Work" })
        {
            var name = folder.ToLowerInvariant();
            Done("migrate", Path.Combine(Packs, folder, "manifest.json"), "--out", name);
            Done("build", name, "--out", name + ".ampack", "--key", keyPath);
            Done("verify", name + ".ampack", "--policy", "trust.json").Should().Contain("kind    definition");
            members.Add(name + ".ampack");
        }

        // The Coding composite, locked to exactly those four, built and verified.
        Done("migrate", Path.Combine(Packs, "Coding", "composite.json"), "--out", "coding");
        Refused("build", "coding", "--out", "unlocked.ampack", "--key", keyPath).Should().Contain("META/lock.json");
        Done(["lock", "coding", .. members]).Should().Contain("Locked 4 members");
        Done("build", "coding", "--out", "coding.ampack", "--key", keyPath);
        Done("verify", "coding.ampack", "--policy", "trust.json").Should().Contain("kind    composite");

        // One set carries the family and the knowledge pack; it verifies member by member.
        Done(["set", "family.ampack", "coding.ampack", .. members, "martialarts.ampack", "--id", "io.example.coding-family", "--version", "1.0.0", "--key", keyPath]);
        var verified = Done("verify", "family.ampack", "--policy", "trust.json", "--ledger", "ledger.json", "--record");
        verified.Should().Contain("Verified set io.example.coding-family 1.0.0").And.Contain("recorded the set and 6 members");
        verified.Split('\n').Count(l => l.TrimStart().StartsWith("member", StringComparison.Ordinal)).Should().Be(6);

        // The host loads what the tool built: the family composes as the compiled Coding composite, the knowledge pack beside it.
        var set = PackFamilies.LoadSet(At("family.ampack"), PackTrust.Load(At("trust.json")));
        set.Families.Should().ContainSingle().Which.Id.Should().Be(CodingPack.Id);
        set.Packs.Should().ContainSingle().Which.Knowledge.Should().NotBeNull();
        var fromTool = new ModuleSetBuilder().AddFamily(set.Families[0]);
        var compiled = new ModuleSetBuilder().AddComposite<CodingPack>();
        fromTool.Modules.Select(m => PackTrialGate.DigestOf(m.Manifest)).Should().Equal(compiled.Modules.Select(m => PackTrialGate.DigestOf(m.Manifest)));
    }

    [Fact]
    public void Diff_names_what_changed_between_two_versions_of_the_real_martial_arts_pack()
    {
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.agentmemory");
        File.WriteAllText(At("trust.json"), PackAuthoring.TrustPolicy(File.ReadAllText(At("keys", "publisher.pub.pem")), "io.agentmemory"));
        Done("migrate", Path.Combine(Packs, "MartialArts"), "--out", "v1");
        Done("build", "v1", "--out", "v1.ampack", "--key", At("keys", "publisher.key.pem"));
        // 1.0.1: one kata's meaning changes, and the kata type gains a property.
        Done("migrate", Path.Combine(Packs, "MartialArts"), "--out", "v2");
        var pack = JsonNode.Parse(File.ReadAllText(At("v2", "pack.json")))!.AsObject();
        pack["version"] = "1.0.1";
        File.WriteAllText(At("v2", "pack.json"), pack.ToJsonString() + "\n");
        var kata = File.ReadAllText(At("v2", "records", "kata.jsonl"));
        File.WriteAllText(At("v2", "records", "kata.jsonl"), kata.Replace("The first of the five Heian ('peaceful mind') kata.",
            "The first of the five Heian kata.", StringComparison.Ordinal));
        var ontology = JsonNode.Parse(File.ReadAllText(At("v2", "ontology.json")))!.AsObject();
        var type = ontology["types"]!.AsArray().Select(t => t!.AsObject()).Single(t => t["name"]!.GetValue<string>() == "MartialArtsKata");
        type["properties"]!.AsObject()["era"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 };
        File.WriteAllText(At("v2", "ontology.json"), ontology.ToJsonString() + "\n");
        Done("build", "v2", "--out", "v2.ampack", "--key", At("keys", "publisher.key.pem"));

        var diff = Done("diff", "v1.ampack", "v2.ampack", "--policy", "trust.json");

        diff.Should().Contain("io.agentmemory.packs.martialarts 1.0.0 -> io.agentmemory.packs.martialarts 1.0.1")
            .And.Contain("~ version: \"1.0.0\" -> \"1.0.1\"")
            .And.Contain("+ types[MartialArtsKata].properties.era")
            .And.Contain("~ MartialArtsKata 'shotokan/heian-shodan': meaning")
            .And.Contain("~1, ");
        Done("diff", "v1.ampack", "v1.ampack").Should().Contain("(the same pack)");
        JsonNode.Parse(Done("diff", "v1.ampack", "v2.ampack", "--json"))!["records"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void Install_list_test_and_uninstall_work_on_a_store_and_the_ledger_outlives_removal()
    {
        // keygen --policy writes the host's trust policy itself, as a host without scripts needs.
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.agentmemory", "--policy", "trust.json").Should().Contain("Added to trust.json (1 key(s))");
        Done("migrate", Path.Combine(Packs, "MartialArts"), "--out", "martialarts");
        Done("build", "martialarts", "--out", "martialarts.ampack", "--key", At("keys", "publisher.key.pem"));

        Done("install", "martialarts.ampack", "--store", "store", "--policy", "trust.json").Should().Contain("Installed io.agentmemory.packs.martialarts 1.0.0");
        Done("list", "--store", "store").Should().Contain("io.agentmemory.packs.martialarts 1.0.0 (knowledge)");
        Done("test", "--store", "store", "--policy", "trust.json").Should().Contain("Loaded 1 pack(s), 93 records");
        Done("uninstall", "io.agentmemory.packs.martialarts", "--store", "store").Should().Contain("the store's ledger keeps it");
        Done("list", "--store", "store").Should().Contain("No packs installed.");
        Ampack("uninstall", "io.agentmemory.packs.martialarts", "--store", "store").Exit.Should().Be(1);
        Ampack("test", "--store", "store", "--policy", "trust.json").Exit.Should().Be(2, "nothing to test is a usage error");
    }

    [Fact]
    public void A_federated_pack_loads_only_for_its_audience_and_a_receipt_vouches_for_one_place_and_those_bytes()
    {
        const string publisher = "https://supplier-a.example/profile/card#org";
        const string buyer = "https://buyer-b.example/profile/card#agent";
        const string stranger = "https://stranger.example/profile/card#me";
        const string location = "https://supplier-a.example/ampack/handoff.ampack";
        var key = Done("keygen", "--out", "keys", "--name", "supplier", "--prefix", "io.supplier-a");
        var entry = JsonNode.Parse(key[(key.IndexOf("Trust-policy entry:", StringComparison.Ordinal) + "Trust-policy entry:".Length)..])!.AsObject();
        entry["webId"] = publisher;
        void Trust(string file, string host) => File.WriteAllText(At(file), new JsonObject
        {
            ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = new JsonArray(entry.DeepClone()), ["host"] = new JsonObject { ["webIds"] = new JsonArray(host) },
        }.ToJsonString());
        Trust("buyer.json", buyer);
        Trust("stranger.json", stranger);
        // init writes the federated extension itself, critical, and refuses an audience that is not WebIDs.
        Ampack("init", "bad", "--id", "io.supplier-a.bad", "--publisher", publisher, "--audience", "not-a-webid").Exit.Should().Be(2);
        Ampack("init", "bad2", "--id", "io.supplier-a.bad", "--audience", buyer).Exit.Should().Be(2, "an audience without a publisher is a usage error");
        Done("init", "handoff", "--id", "io.supplier-a.handoff", "--publisher", publisher, "--audience", buyer, "--purpose", "Supply-chain handoff");
        JsonNode.Parse(File.ReadAllText(At("handoff", "pack.json")))!["extensions"]!["critical"]![0]!.GetValue<string>().Should().Be("federation");
        Done("build", "handoff", "--out", "handoff.ampack", "--key", At("keys", "supplier.key.pem"));

        Done("verify", "handoff.ampack", "--policy", "buyer.json").Should().Contain("publisher " + publisher).And.Contain("for     " + buyer);
        Refused("verify", "handoff.ampack", "--policy", "stranger.json").Should().Contain("this host is " + stranger);
        Done("inspect", "handoff.ampack").Should().Contain("Federated: publisher " + publisher);
        Done("lint", "handoff.ampack", "--policy", "buyer.json").Should().Contain("federated for 1 WebID(s), published by " + publisher);

        Done("receipt", "sign", "handoff.ampack", "--location", location, "--key", At("keys", "supplier.key.pem"),
            "--denied", "anonymous," + stranger, "--allowed", buyer, "--out", "handoff.receipt.json").Should().Contain("for     " + buyer + " (wac)");
        Done("receipt", "verify", "handoff.ampack", "handoff.receipt.json", "--location", location, "--policy", "buyer.json")
            .Should().Contain("probed  denied anonymous, " + stranger + "; allowed " + buyer);
        Refused("receipt", "verify", "handoff.ampack", "handoff.receipt.json", "--location", "https://mirror.example/handoff.ampack", "--policy", "buyer.json")
            .Should().Contain("not at https://mirror.example/handoff.ampack");
        Refused("receipt", "sign", "handoff.ampack", "--location", location, "--key", At("keys", "supplier.key.pem"), "--audience", "public", "--out", "wide.json")
            .Should().Contain("can only narrow");

        // The host's install path enforces the audience too: the buyer's store takes the pack, the stranger's refuses it.
        Done("install", "handoff.ampack", "--store", "buyer-store", "--policy", "buyer.json").Should().Contain("Installed io.supplier-a.handoff");
        Refused("install", "handoff.ampack", "--store", "stranger-store", "--policy", "stranger.json").Should().Contain("this host is " + stranger);

        // Text a pack carries reaches the terminal without control or direction characters, in what is shown and in what a
        // refusal quotes (FED-review round 2).
        Done("init", "loud", "--id", "io.supplier-a.loud", "--publisher", publisher, "--audience", buyer, "--purpose", "Supply\u001b[31m‏chain");
        Done("build", "loud", "--out", "loud.ampack", "--key", At("keys", "supplier.key.pem"));
        Done("verify", "loud.ampack", "--policy", "buyer.json").Should().Contain("purpose Supply?[31m?chain").And.NotContain("\u001b").And.NotContain("‏");
        var manifest = JsonNode.Parse(File.ReadAllText(At("loud", "pack.json")))!.AsObject();
        manifest["note\u001b[2J"] = "x";
        File.WriteAllText(At("loud", "pack.json"), manifest.ToJsonString());
        Refused("build", "loud", "--out", "louder.ampack", "--key", At("keys", "supplier.key.pem")).Should().Contain("note?[2J").And.NotContain("\u001b");
    }

    [Fact]
    public void An_option_a_command_does_not_take_is_a_usage_error_and_help_and_version_answer()
    {
        // Review 1, H2: a typo must never drop a trust input silently (--revocation for --revocations).
        var (exit, output) = Ampack("install", "x.ampack", "--store", "s", "--policy", "t.json", "--revocation", "r.json");
        exit.Should().Be(2);
        output.Should().Contain("ampack install takes no --revocation; did you mean --revocations?");
        Ampack("verify", "x.ampack", "--policy", "t.json", "--legder", "l.json").Output.Should().Contain("did you mean --ledger?");
        Ampack("gate", "x.ampack", "--require-trial-for", "io.x").Exit.Should().Be(2);
        Ampack("verify", "x.ampack", "--policy", "t.json", "--record").Output.Should().Contain("--record writes the ledger");
        // ampack <command> --help and ampack --version.
        var (helpExit, help) = Ampack("verify", "--help");
        helpExit.Should().Be(0);
        help.Should().Contain("verify <folder|file> --policy").And.Contain("Options: --allow-downgrade, --explain, --ledger");
        Ampack("--version").Output.Should().StartWith("ampack ").And.Contain("agentmemory.pack/1.2");
        // An unexpected failure is said in one line with exit 4, never a stack trace; malformed input is a refusal (1).
        File.WriteAllText(At("array.json"), "[]");
        var (policyExit, policyOutput) = Ampack("verify", "x.ampack", "--policy", "array.json");
        policyExit.Should().Be(1);
        policyOutput.Should().NotContain("   at ");
    }

    [Fact]
    public void A_key_kept_encrypted_signs_only_with_its_passphrase_and_only_its_owner_reads_the_file()
    {
        // Review 1, M12: keygen --passphrase-env writes an encrypted PKCS#8 key; every command opens it with AMPACK_KEY_PASSPHRASE.
        var passphrase = new Dictionary<string, string> { ["KEY_PASSPHRASE"] = "correct horse battery staple" };
        var (exit, output) = Ampack(passphrase, "keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.example", "--passphrase-env", "KEY_PASSPHRASE");
        exit.Should().Be(0, output);
        File.ReadAllText(At("keys", "publisher.key.pem")).Should().Contain("BEGIN ENCRYPTED PRIVATE KEY");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(At("keys", "publisher.key.pem")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Done("init", "notes", "--id", "io.example.notes");
        Ampack("build", "notes", "--out", "notes.ampack", "--key", At("keys", "publisher.key.pem")).Output.Should().Contain("AMPACK_KEY_PASSPHRASE");
        Ampack(new Dictionary<string, string> { ["AMPACK_KEY_PASSPHRASE"] = "wrong" }, "build", "notes", "--out", "notes.ampack", "--key", At("keys", "publisher.key.pem"))
            .Exit.Should().Be(2);
        Ampack(new Dictionary<string, string> { ["AMPACK_KEY_PASSPHRASE"] = "correct horse battery staple" }, "build", "notes", "--out", "notes.ampack", "--key", At("keys", "publisher.key.pem"))
            .Exit.Should().Be(0);
        Ampack(new Dictionary<string, string> { ["SHORT"] = "short" }, "keygen", "--out", "keys2", "--passphrase-env", "SHORT").Exit.Should().Be(2);
        // The default prefix is said, not silent.
        Done("keygen", "--out", "keys3").Should().Contain("Prefix  io.example (the default");
    }

    [Fact]
    public void A_receipt_is_signed_only_with_a_key_that_signed_the_pack_as_its_publisher()
    {
        // Review 1, L13.
        const string publisher = "https://supplier-a.example/profile/card#org";
        Done("keygen", "--out", "keys", "--name", "supplier", "--prefix", "io.supplier-a");
        Done("keygen", "--out", "keys", "--name", "other", "--prefix", "io.supplier-a");
        Done("init", "handoff", "--id", "io.supplier-a.handoff", "--publisher", publisher, "--audience", "https://buyer-b.example/profile/card#agent");
        Done("build", "handoff", "--out", "handoff.ampack", "--key", At("keys", "supplier.key.pem"));

        Refused("receipt", "sign", "handoff.ampack", "--location", "https://supplier-a.example/a.ampack", "--key", At("keys", "other.key.pem"), "--out", "r.json")
            .Should().Contain("did not sign this pack as its publisher");
        Done("receipt", "sign", "handoff.ampack", "--location", "https://supplier-a.example/a.ampack", "--key", At("keys", "supplier.key.pem"), "--out", "r.json",
            "--denied", "anonymous", "--allowed", "https://buyer-b.example/profile/card#agent");
    }

    [Fact]
    public void Text_from_a_pack_reaches_the_terminal_shown_in_explain_scan_and_diff()
    {
        // Review 1, M11: the commands used to vet a suspect pack never pass its control characters through.
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.example");
        Done("init", "loud", "--id", "io.example.loud");
        var record = Directory.EnumerateFiles(At("loud", "records")).Single();
        File.WriteAllText(record, "{\"type\":\"LoudNote\",\"key\":\"n\\u001b[2J1\",\"properties\":{\"title\":\"t\",\"text\":\"x\\u001b]0;owned\\u0007\"}}\n");
        Done("build", "loud", "--out", "loud.ampack", "--key", At("keys", "publisher.key.pem"));
        Done("init", "quiet", "--id", "io.example.loud");
        Done("build", "quiet", "--out", "quiet.ampack", "--key", At("keys", "publisher.key.pem"));

        foreach (var output in new[] { Ampack("scan", "loud.ampack").Output, Ampack("diff", "quiet.ampack", "loud.ampack").Output,
            Ampack("verify", "loud.ampack", "--policy", "nothing.json", "--explain").Output })
            output.Should().NotContain("\u001b").And.NotContain("\u0007");
    }

    [Fact]
    public void A_card_travels_in_the_pack_and_an_evaluator_s_certificate_beside_it_until_a_host_that_trusts_it_admits_the_pack()
    {
        // Three parties: the publisher, an independent evaluator (its own key, role evaluator), and the host.
        JsonObject Entry(string output) => JsonNode.Parse(output[(output.IndexOf("Trust-policy entry:", StringComparison.Ordinal) + "Trust-policy entry:".Length)..])!.AsObject();
        var publisher = Entry(Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.example"));
        var evaluator = Entry(Done("keygen", "--out", "keys", "--name", "evaluator", "--prefix", "io.example", "--role", "evaluator"));
        Done("keygen", "--out", "keys", "--name", "lab-host", "--prefix", "io.example");
        Done("keygen", "--out", "keys", "--name", "host", "--prefix", "io.example");
        File.WriteAllText(At("trust.json"), new JsonObject { ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = new JsonArray(publisher.DeepClone(), evaluator) }.ToJsonString());
        File.WriteAllText(At("publisher-only.json"), new JsonObject { ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = new JsonArray(publisher.DeepClone()) }.ToJsonString());

        // init writes a card from a template; lint says so until it is filled in.
        Done("init", "notes", "--id", "io.example.notes", "--owner", "Example Team");
        var card = JsonNode.Parse(File.ReadAllText(At("notes", "card.json")))!.AsObject();
        card["owner"]!["name"]!.GetValue<string>().Should().Be("Example Team");
        Directory.CreateDirectory(At("notes", "evals"));
        File.WriteAllText(At("notes", "evals", "trial.json"), """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRate": 0.5 } }""");
        Done("build", "notes", "--out", "notes.ampack", "--key", At("keys", "publisher.key.pem"));
        Done("lint", "notes.ampack", "--policy", "trust.json").Should().Contain("card.template");
        card["intendedUse"] = "Notes for the example team's study coach.";
        card["limitations"] = "Not for anyone else; may be out of date.";
        card["boundaries"] = new JsonArray("Do not export the notes.");
        File.WriteAllText(At("notes", "card.json"), card.ToJsonString());
        Done("build", "notes", "--out", "notes.ampack", "--key", At("keys", "publisher.key.pem"));
        var verified = Done("verify", "notes.ampack", "--policy", "trust.json");
        verified.Should().Contain("card    Example Team: Notes for the example team's study coach.").And.Contain("boundaries: Do not export the notes.");
        var digest = verified.Split('\n').Single(l => l.TrimStart().StartsWith("digest", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[1];
        var declarations = verified.Split('\n').Single(l => l.TrimStart().StartsWith("declarations", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[1];
        Done("inspect", "notes.ampack").Should().Contain("Card    Example Team");

        // The evaluator ran the trial on its own tasks; its gate signed the decision with the lab's host key.
        Refused("certify", "notes.ampack", "--policy", "trust.json", "--activations", "lab-decisions", "--host-key", At("keys", "lab-host.pub.pem"),
            "--key", At("keys", "evaluator.key.pem"), "--evaluator", "Example Lab", "--task-source", "40 changes", "--out", "x.ampack").Should().Contain("No trial decision for exactly this pack");
        var pack = PackLoader.Load(At("notes.ampack"), PackTrust.Load(At("trust.json")));
        PackTrialGate.DigestOf(pack.Manifest).Should().Be(declarations, "verify prints what a decision binds");
        using (var labHost = System.Security.Cryptography.ECDsa.Create())
        {
            labHost.ImportFromPem(File.ReadAllText(At("keys", "lab-host.key.pem")));
            var arm = new PackTrialMeasurement { Tasks = 40, Hits = 24, Offered = 120, Correct = 30, P95Ms = 12.5 };
            var unsaid = PackTrialGate.Decide(pack.Manifest, arm, arm with { Hits = 0 }, DateTimeOffset.UtcNow.AddHours(-2), pack.Digest);
            PackActivationFolder.Write(At("lab-decisions"), unsaid, labHost);
            Refused("certify", "notes.ampack", "--policy", "trust.json", "--activations", "lab-decisions", "--host-key", At("keys", "lab-host.pub.pem"),
                "--key", At("keys", "evaluator.key.pem"), "--evaluator", "Example Lab", "--task-source", "40 changes", "--out", "x.ampack")
                .Should().Contain("does not say how its tasks were drawn", "review 3: only a trial at each commit's parent is certified");
            PackActivationFolder.Write(At("lab-decisions"), PackTrialGate.Decide(pack.Manifest, arm, arm with { Hits = 0 }, DateTimeOffset.UtcNow.AddHours(-1), pack.Digest)
                with { History = "parent" }, labHost);
        }
        Done("certify", "notes.ampack", "--policy", "trust.json", "--activations", "lab-decisions", "--host-key", At("keys", "lab-host.pub.pem"),
            "--key", At("keys", "evaluator.key.pem"), "--evaluator", "Example Lab", "--task-source", "40 changes from the lab's repository", "--out", "certified.ampack")
            .Should().Contain("Certified io.example.notes 0.1.0 (passed").And.Contain($"digest  {digest} (unchanged)");

        // A host reads it: verified, by the evaluator's key, for this pack; inspection lists it unverified.
        Done("certificate", "verify", "certified.ampack", "--policy", "trust.json")
            .Should().Contain("by      Example Lab").And.Contain("result  passed: hitRate 0.6 (met 0.5)").And.Contain("40 tasks from 40 changes from the lab's repository")
            .And.Contain("tool    agentmemory-pack-gate " + typeof(PackTrialGate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0],
                "the certificate names the version of the library that holds the gate");
        Done("verify", "certified.ampack", "--policy", "trust.json").Should().Contain($"digest  {digest}").And.Contain("certificate passed, by Example Lab");
        Done("inspect", "certified.ampack").Should().Contain("certificate  META/attestations/");
        Refused("certificate", "verify", "certified.ampack", "--policy", "publisher-only.json").Should().Contain("not in the trust policy");
        Done("verify", "certified.ampack", "--policy", "publisher-only.json").Should().Contain("not trusted here", "an untrusted certificate never refuses the pack");

        // A host that trusts certificates for io.example admits it on the certificate, signed with its own key.
        Refused("admit", "certified.ampack", "--policy", "publisher-only.json", "--host-signing-key", At("keys", "host.key.pem"), "--activations", "host-decisions")
            .Should().Contain("None of the 1 certificate(s)");
        Done("admit", "certified.ampack", "--policy", "trust.json", "--host-signing-key", At("keys", "host.key.pem"), "--activations", "host-decisions")
            .Should().Contain("Admitted io.example.notes 0.1.0").And.Contain("basis   certificate sha256:").And.Contain("TrustCertificatesFor");
        var record = Directory.EnumerateFiles(At("host-decisions")).Should().ContainSingle().Subject;
        record.Should().EndWith("-certificate.dsse.json");
        new PackActivationFolder(At("host-decisions"), File.ReadAllText(At("keys", "host.pub.pem"))).Find("io.example.notes", ModuleVersion.Parse("0.1.0"), declarations, digest)!
            .Basis!.Evaluator.Should().Be("Example Lab");

        // Both modes, from the host's signed decisions alone: proven here by default, the certificate only where trusted.
        string[] gate = ["gate", "certified.ampack", "--policy", "trust.json", "--activations", "host-decisions", "--host-key", At("keys", "host.pub.pem")];
        Done(gate).Should().Contain("off  io.example.notes 0.1.0  it was admitted on Example Lab's certificate").And.Contain("needs its own trial");
        Done([.. gate, "--trust-certificates-for", "io.example"]).Should().Contain("on   io.example.notes 0.1.0  on Example Lab's certificate");
        Ampack([.. gate, "--trust-certificates-for", "io..example"]).Exit.Should().Be(2);

        // Lint tells the publisher what a host can do with it.
        Done("lint", "certified.ampack", "--policy", "trust.json").Should().Contain("certificate.admits");
    }

    [Fact]
    public void A_key_from_the_environment_a_protected_file_or_an_external_signer_signs_as_a_file_key_does()
    {
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.example");
        File.WriteAllText(At("trust.json"), PackAuthoring.TrustPolicy(File.ReadAllText(At("keys", "publisher.pub.pem")), "io.example"));
        Done("init", "notes", "--id", "io.example.notes");
        var pem = File.ReadAllText(At("keys", "publisher.key.pem"));
        var signer = $"dotnet \"{Tool}\" sign-bytes --key \"{At("keys", "publisher.key.pem")}\"";

        // env: a PEM in an environment variable, never on disk.
        Ampack(new Dictionary<string, string> { ["AMPACK_TEST_KEY"] = pem }, "build", "notes", "--out", "env.ampack", "--key", "env:AMPACK_TEST_KEY").Exit.Should().Be(0);
        Done("verify", "env.ampack", "--policy", "trust.json");
        // --signer: the command signs; DER or the r||s form KMS tools print; ampack checks the signature against --public.
        Done("build", "notes", "--out", "der.ampack", "--signer", signer, "--public", At("keys", "publisher.pub.pem"));
        Done("verify", "der.ampack", "--policy", "trust.json");
        Done("build", "notes", "--out", "p1363.ampack", "--signer", signer + " --p1363", "--public", At("keys", "publisher.pub.pem"));
        Done("verify", "p1363.ampack", "--policy", "trust.json");
        Done("keygen", "--out", "other", "--name", "other", "--prefix", "io.example");
        var wrong = Ampack("build", "notes", "--out", "wrong.ampack", "--signer", signer, "--public", At("other", "other.pub.pem"));
        wrong.Exit.Should().NotBe(0);
        wrong.Output.Should().Contain("does not verify with the public key");
        File.Exists(At("wrong.ampack")).Should().BeFalse("nothing is written when the signer signs with another key");

        // dpapi: a private key encrypted for this Windows user.
        if (OperatingSystem.IsWindows())
        {
            Done("keygen", "--out", "protected", "--name", "publisher", "--prefix", "io.example", "--protect", "dpapi");
            File.Exists(At("protected", "publisher.key.pem")).Should().BeFalse("no plaintext private key");
            File.WriteAllText(At("trust-protected.json"), PackAuthoring.TrustPolicy(File.ReadAllText(At("protected", "publisher.pub.pem")), "io.example"));
            Done("build", "notes", "--out", "dpapi.ampack", "--key", "dpapi:" + At("protected", "publisher.key.dpapi"));
            Done("verify", "dpapi.ampack", "--policy", "trust-protected.json");
        }
    }

    [Fact]
    public void Review3_and_4_a_typo_or_a_wrong_shape_is_refused_before_anything_is_written()
    {
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.agentmemory", "--policy", "trust.json");
        Done("migrate", Path.Combine(Packs, "MartialArts"), "--out", "martialarts");
        Done("build", "martialarts", "--out", "martialarts.ampack", "--key", At("keys", "publisher.key.pem"));

        // A store a command only reads is there already: a mistyped one is refused, and nothing is created.
        Ampack("list", "--store", "sotre").Exit.Should().Be(2);
        Ampack("verify", "martialarts.ampack", "--policy", "trust.json", "--store", "sotre").Exit.Should().Be(2);
        Ampack("ledger", "--retire-revocations", "--store", "sotre").Exit.Should().Be(2);
        Directory.Exists(At("sotre")).Should().BeFalse("no command that only reads a store creates one");
        // A ledger named but missing needs --record, with revocation lists too, and ledger itself never starts one.
        Ampack("verify", "martialarts.ampack", "--policy", "trust.json", "--ledger", "ledgr.json", "--revocations", "none.json").Exit.Should().Be(2);
        Ampack("ledger", "--retire-revocations", "--ledger", "ledgr.json").Exit.Should().Be(2);
        File.Exists(At("ledgr.json")).Should().BeFalse();
        // Arguments are counted per subaction, not by the command's largest count.
        Ampack("receipt", "sign", "martialarts.ampack", "extra.ampack", "--location", "https://example.com/p.ampack", "--key", At("keys", "publisher.key.pem"))
            .Exit.Should().Be(2);
        // --allow-remote applies the remote rules on loopback too: a bearer token is asked for.
        Ampack("serve", "martialarts.ampack", "--policy", "trust.json", "--http", "http://localhost:5199/", "--allow-remote").Output
            .Should().Contain("bearer token");
    }

    [Fact]
    public async Task Serve_answers_over_REST_with_the_same_answers_as_its_MCP_tools()
    {
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.agentmemory", "--policy", "trust.json");
        Done("migrate", Path.Combine(Packs, "MartialArts"), "--out", "martialarts");
        Done("build", "martialarts", "--out", "martialarts.ampack", "--key", At("keys", "publisher.key.pem"));
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        Ampack("serve", "martialarts.ampack", "--policy", "trust.json", "--http", "http://example.com:5199/").Exit.Should().Be(2, "beyond this machine needs --allow-remote");
        Ampack("serve", "martialarts.ampack", "--policy", "trust.json", "--http", "http://example.com:5199/", "--allow-remote").Exit
            .Should().Be(2, "beyond this machine needs a bearer token (review 1, M1)");
        Ampack(new Dictionary<string, string> { ["AMPACK_SERVE_TOKEN"] = "short" }, "serve", "martialarts.ampack", "--policy", "trust.json",
            "--http", $"http://localhost:{port}/").Exit.Should().Be(2, "a token is at least 32 characters");

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root,
        };
        foreach (var arg in new[] { Tool, "serve", "martialarts.ampack", "--policy", "trust.json", "--http", $"http://localhost:{port}/" }) start.ArgumentList.Add(arg);
        using var server = Process.Start(start)!;
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            async Task<(System.Net.HttpStatusCode Status, JsonNode Body)> Send(HttpMethod method, string path, string? json = null)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var message = new HttpRequestMessage(method, path) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
                        using var response = await http.SendAsync(message);
                        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
                    }
                    catch (HttpRequestException) when (attempt < 50) { await Task.Delay(200); } // the server is still starting
                }
            }

            var meta = await Send(HttpMethod.Get, "v1/meta");
            meta.Body["records"]!.GetValue<int>().Should().Be(93);
            var packs = (await Send(HttpMethod.Get, "v1/packs")).Body.AsArray();
            packs.Single()!["id"]!.GetValue<string>().Should().Be("io.agentmemory.packs.martialarts");
            var asked = await Send(HttpMethod.Post, "v1/packs/io.agentmemory.packs.martialarts/views/kata/ask", """{"text":"Which kata for 8th kyu? Heian Shodan"}""");
            asked.Status.Should().Be(System.Net.HttpStatusCode.OK);
            asked.Body[0]!["key"]!.GetValue<string>().Should().Be("shotokan/heian-shodan");
            asked.Body[0]!["source"]!["digest"]!.GetValue<string>().Should().Be(packs.Single()!["digest"]!.GetValue<string>());
            (await Send(HttpMethod.Post, "v1/packs/io.agentmemory.packs.martialarts/views/ladder/ask", """{"key":"shotokan/8-kyu"}""")).Body.AsArray()
                .Should().ContainSingle();
            var nowhere = await Send(HttpMethod.Post, "v1/walk", "{}");
            nowhere.Status.Should().Be(System.Net.HttpStatusCode.BadRequest);
            nowhere.Body["error"]!.GetValue<string>().Should().Contain("at least one path or symbol");
            (await Send(HttpMethod.Post, "v1/walk", "not json")).Status.Should().Be(System.Net.HttpStatusCode.BadRequest);
            (await Send(HttpMethod.Post, "v1/walk", """{"paths":"one"}""")).Body["error"]!.GetValue<string>().Should().Contain("list of strings");
            (await Send(HttpMethod.Delete, "v1/packs")).Status.Should().Be(System.Net.HttpStatusCode.NotFound, "nothing writes");
            (await Send(HttpMethod.Post, "v1/packs/io.agentmemory.packs.martialarts/views/kata/ask", "{\"text\":\"" + new string('x', 70_000) + "\"}")).Status
                .Should().Be(System.Net.HttpStatusCode.BadRequest, "bodies are capped");
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Review1_M1_a_served_token_is_asked_of_every_request()
    {
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.agentmemory", "--policy", "trust.json");
        Done("migrate", Path.Combine(Packs, "MartialArts"), "--out", "martialarts");
        Done("build", "martialarts", "--out", "martialarts.ampack", "--key", At("keys", "publisher.key.pem"));
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root,
        };
        start.Environment["AMPACK_SERVE_TOKEN"] = token;
        foreach (var arg in new[] { Tool, "serve", "martialarts.ampack", "--policy", "trust.json", "--http", $"http://localhost:{port}/" }) start.ArgumentList.Add(arg);
        using var server = Process.Start(start)!;
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            async Task<System.Net.HttpStatusCode> Get(string? bearer)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var message = new HttpRequestMessage(HttpMethod.Get, "v1/meta");
                        if (bearer is not null) message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                        using var response = await http.SendAsync(message);
                        return response.StatusCode;
                    }
                    catch (HttpRequestException) when (attempt < 50) { await Task.Delay(200); } // the server is still starting
                }
            }

            (await Get(null)).Should().Be(System.Net.HttpStatusCode.Unauthorized);
            (await Get(token + "x")).Should().Be(System.Net.HttpStatusCode.Unauthorized);
            (await Get(token)).Should().Be(System.Net.HttpStatusCode.OK);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
    }

    [Fact]
    public void Review1_I8_trust_add_list_and_remove_keep_a_policy_from_the_command_line()
    {
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.example");
        Done("keygen", "--out", "keys", "--name", "curator", "--prefix", "io.example");
        var publisher = Done("trust", "add", "--public", At("keys", "publisher.pub.pem"), "--policy", "trust.json", "--prefix", "io.example").Trim().Split(' ')[^1];
        var curator = Done("trust", "add", "--public", At("keys", "curator.pub.pem"), "--policy", "trust.json", "--prefix", "io.example.notes",
            "--role", "curator").Trim().Split(' ')[^1];
        Ampack("trust", "add", "--public", At("keys", "publisher.pub.pem"), "--policy", "trust.json", "--prefix", "io.example").Exit.Should().Be(2, "it is there already");

        var listed = Done("trust", "list", "--policy", "trust.json");
        listed.Should().Contain("2 key(s)").And.Contain(publisher).And.Contain(curator).And.Contain("io.example.notes");

        Ampack("trust", "remove", publisher[..8], "--policy", "trust.json").Exit.Should().Be(2, "a key is named by at least 16 characters of its id");
        Done("trust", "remove", publisher[..16], "--policy", "trust.json");
        Done("trust", "list", "--policy", "trust.json").Should().Contain("1 key(s)").And.NotContain(publisher).And.Contain(curator);
    }

    [Fact]
    public async Task Serve_answers_an_MCP_client_over_standard_input_and_output()
    {
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.agentmemory");
        File.WriteAllText(At("trust.json"), PackAuthoring.TrustPolicy(File.ReadAllText(At("keys", "publisher.pub.pem")), "io.agentmemory"));
        Done("migrate", Path.Combine(Packs, "MartialArts"), "--out", "martialarts");
        Done("build", "martialarts", "--out", "martialarts.ampack", "--key", At("keys", "publisher.key.pem"));

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _root,
        };
        foreach (var arg in new[] { Tool, "serve", "martialarts.ampack", "--policy", "trust.json" }) start.ArgumentList.Add(arg);
        using var server = Process.Start(start)!;
        var errors = server.StandardError.ReadToEndAsync();
        try
        {
            async Task<JsonNode> Call(int id, string method, JsonNode? parameters)
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
                if (parameters is not null) request["params"] = parameters;
                await server.StandardInput.WriteLineAsync(request.ToJsonString());
                await server.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var line = await server.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("ampack serve closed its output.");
                    if (JsonNode.Parse(line) is JsonObject message && message["id"]?.GetValue<int>() == id) return message;
                }
            }
            string Text(JsonNode response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();

            var hello = await Call(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
            });
            hello["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("ampack");
            await server.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var tools = await Call(2, "tools/list", null);
            tools["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Should().BeEquivalentTo(["pack_list", "pack_ask", "pack_walk"]);

            var listed = JsonNode.Parse(Text(await Call(3, "tools/call", new JsonObject { ["name"] = "pack_list", ["arguments"] = new JsonObject() })))!.AsArray();
            listed.Single()!["id"]!.GetValue<string>().Should().Be("io.agentmemory.packs.martialarts");

            var asked = JsonNode.Parse(Text(await Call(4, "tools/call", new JsonObject
            {
                ["name"] = "pack_ask",
                ["arguments"] = new JsonObject { ["pack"] = "io.agentmemory.packs.martialarts", ["view"] = "kata", ["text"] = "Which kata for 8th kyu? Heian Shodan" },
            })))!.AsArray();
            asked[0]!["key"]!.GetValue<string>().Should().Be("shotokan/heian-shodan");
            asked[0]!["source"]!["digest"]!.GetValue<string>().Should().Be(listed.Single()!["digest"]!.GetValue<string>());
            asked[0]!["source"]!["file"]!.GetValue<string>().Should().Be("records/kata.jsonl");

            var nowhere = JsonNode.Parse(Text(await Call(5, "tools/call", new JsonObject { ["name"] = "pack_walk", ["arguments"] = new JsonObject() })))!;
            nowhere["error"]!.GetValue<string>().Should().Contain("at least one path or symbol");
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
        (await errors).Should().Contain("ampack serve: 1 pack(s), 93 records");
    }
}
