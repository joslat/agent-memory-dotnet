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

    private (int Exit, string Output) Ampack(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root,
        };
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
        Done("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.agentmemory");
        File.WriteAllText(At("trust.json"), PackAuthoring.TrustPolicy(File.ReadAllText(At("keys", "publisher.pub.pem")), "io.agentmemory"));
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
