using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The door matrix (review 7's root-cause analysis, C1 and C4: a new door, or an old one nobody looked at, started without the
/// rules the other doors had earned). Every <c>ampack</c> command, enumerated from the tool's own command table, runs on a hostile
/// corpus: an unsigned pack with an 8 MiB JSON line, a signed pack whose strings carry terminal escapes and bidi controls, and a
/// signed later minor whose unknown field names carry them. Each run refuses or answers, never crashes (exit 4), never prints a raw
/// control or bidi character, and stays within a memory budget. A command that takes no pack is exempt by name, with its reason,
/// so a new command fails here until it is placed in the matrix or exempted.
/// </summary>
public sealed class PackDoorMatrixTests : IDisposable
{
    // The tool's own baseline is about 70 MiB; the format bounds unverified content at 64 MiB in all and 1 MiB a file, and D2 took
    // 241 MiB on two 8 MiB files before it was bounded.
    private const long MemoryBudget = 256L << 20;

    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["init"] = "writes a new authoring folder; reads no pack",
        ["keygen"] = "writes a key pair; reads no pack",
        ["erase"] = "writes and signs an erasure; reads no pack",
        ["ledger"] = "reads the host's own ledger",
        ["sign-bytes"] = "signs standard input for an external signer; reads no pack",
        ["uninstall"] = "removes a pack from the host's own store by id",
        ["list"] = "lists the host's own store",
        ["revoke"] = "writes a revocation list; reads no pack",
        ["evidence"] = "reads the host's own decision records",
        ["solid"] = "network: covered by the Solid tests against a local server",
        ["trust"] = "edits the host's own trust policy",
    };

    private readonly string _root = Directory.CreateTempSubdirectory("ampack-doors-").FullName;
    private readonly ITestOutputHelper _log;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyPem, _pubPem, _policy, _activations, _store;

    public PackDoorMatrixTests(ITestOutputHelper log)
    {
        _log = log;
        _keyPem = Write("keys/publisher.key.pem", _key.ExportPkcs8PrivateKeyPem());
        _pubPem = Write("keys/publisher.pub.pem", _key.ExportSubjectPublicKeyInfoPem());
        _policy = Write("trust.json", PackAuthoringPolicy(_key));
        _activations = Directory.CreateDirectory(Path.Combine(_root, "activations")).FullName;
        _store = Path.Combine(_root, "store");
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Every_command_meets_every_hostile_pack_with_a_refusal_or_an_answer_that_is_bounded_and_escaped()
    {
        var commands = CommandTable();
        var doors = Doors("P", "P2", "F").Keys.ToHashSet(StringComparer.Ordinal);
        commands.Except(doors).Except(Exempt.Keys).Should().BeEmpty("every command is a door in the matrix or exempt with its reason");
        doors.Union(Exempt.Keys).Except(commands).Should().BeEmpty("the matrix names only commands the tool has");

        var problems = new List<string>();
        foreach (var (name, files) in Corpus())
        {
            var pack = Write($"{name}/pack.ampack", StrictZip.Write(files));
            var folder = Path.Combine(_root, name, "folder");
            foreach (var (path, bytes) in files) Write(Path.Combine(name, "folder", path), bytes);
            foreach (var (command, args) in Doors(pack, pack, folder))
            {
                var run = Run([command, .. args]);
                _log.WriteLine($"{name,-12} {command,-12} exit {run.Exit} peak {run.Peak >> 20} MiB {(run.TimedOut ? "(server, stopped)" : "")}");
                if (run.Exit == 4 || run.Output.Contains("failed unexpectedly", StringComparison.Ordinal))
                    problems.Add($"{name} {command}: a defect, not a refusal: {Tail(run.Output)}");
                // A usage error means the matrix never reached the door: its arguments are wrong, not the door right.
                if (run.Exit == 2) problems.Add($"{name} {command}: a usage error, so the door was never reached: {Tail(run.Output)}");
                if (Raw(run.Output) is { } raw) problems.Add($"{name} {command}: printed {raw} raw");
                if (run.Peak > MemoryBudget) problems.Add($"{name} {command}: peak {run.Peak >> 20} MiB, over the {MemoryBudget >> 20} MiB budget");
            }
        }
        foreach (var problem in problems) _log.WriteLine(problem);
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Review7_L7_countersign_lock_and_set_act_only_on_a_pack_whose_signatures_hold()
    {
        // An assurer could countersign, a lock pin and a set carry a pack nobody had verified.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = """{"format":"agentmemory.pack/1.2","id":"io.example.unsigned","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)"}"""u8.ToArray(),
            ["records/notes.jsonl"] = [.. """{"type":"Note","key":"n1","text":"plain"}"""u8, .. "\n"u8],
        };
        files[PackPaths.FilesList] = FilesList.Build("io.example.unsigned", "1.0.0", files);
        var pack = Write("unsigned/pack.ampack", StrictZip.Write(files));
        var folder = Directory.CreateDirectory(Path.Combine(_root, "unsigned", "composite")).FullName;

        foreach (var (command, args) in Doors(pack, pack, folder).Where(d => d.Key is "countersign" or "lock" or "set"))
        {
            var run = Run([command, .. args]);
            run.Exit.Should().Be(1, $"{command} refuses: {run.Output}");
            run.Output.Should().Contain("No valid publisher signature", command);
            var withoutPolicy = Run([command, .. args.TakeWhile(a => a != "--policy")]);
            withoutPolicy.Exit.Should().Be(2, $"{command} needs the policy it verifies under: {withoutPolicy.Output}");
        }

        // Review 8, F3: a set whose outer signature holds and whose member a stranger signed. The host refuses it whole, so the doors
        // that act only on a verified pack refuse it too, and scan and diff, given the policy, report it refused.
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var strangerSigner = PackSigner.FromPem(stranger.ExportPkcs8PrivateKeyPem());
        files[Dsse.PathFor("publisher", strangerSigner.KeyId)] = Dsse.Sign(files[PackPaths.FilesList], "io.example.unsigned", "1.0.0", "publisher",
            DateTimeOffset.UtcNow.AddMinutes(-5), strangerSigner);
        var (set, _) = PackBuilder.Build(PackSets.Source("io.example.strangers", "1.0.0",
            new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { ["io.example.unsigned"] = files }));
        var setPack = Write("unsigned/set.ampack", StrictZip.Write(Signed("io.example.strangers", set)));
        foreach (var (command, args) in Doors(setPack, setPack, folder).Where(d => d.Key is "countersign" or "lock" or "set"))
        {
            var run = Run([command, .. args]);
            run.Exit.Should().Be(1, $"{command} refuses a set whose member its host refuses: {run.Output}");
            run.Output.Should().Contain("packs/io.example.unsigned/: No valid publisher signature", command);
        }
        foreach (var command in new[] { "scan", "diff" })
        {
            var run = Run([command, .. Doors(setPack, setPack, folder)[command], "--policy", _policy]);
            run.Exit.Should().Be(1, $"{command} with a policy refuses the set: {run.Output}");
        }
    }
    [Fact]
    public void Review8_lint_bounds_what_nobody_verified_before_it_parses_any_of_it()
    {
        // UNVERIFIED-BOUNDED at lint (the mutant suite: the matrix's 40 MiB line was refused by later checks too, so removing the
        // bound changed no verdict). Seventy JSONL files of about 1 MiB, each valid: only the bound on unverified content in all
        // refuses them, and it must before any is parsed.
        var folder = Path.Combine(_root, "many", "folder");
        Write("many/folder/pack.json", """{"format":"agentmemory.pack/1.2","id":"io.example.many","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)"}""");
        var line = Encoding.UTF8.GetBytes("{\"type\":\"Note\",\"key\":\"n\",\"text\":\"" + new string('x', 1_000_000) + "\"}\n");
        for (var i = 0; i < 70; i++) Write($"many/folder/records/part{i:00}.jsonl", line);

        var run = Run(["lint", folder]);
        run.Exit.Should().Be(1, run.Output);
        run.Output.Should().Contain("a pack read without verifying it is at most 64 MiB");
    }

    // Each door with the arguments that reach its pack input: the pack, a second pack, or an authoring folder.
    private Dictionary<string, string[]> Doors(string pack, string other, string folder)
    {
        var out_ = Path.Combine(_root, "out", Guid.NewGuid().ToString("N"));
        return new(StringComparer.Ordinal)
        {
            ["lint"] = [pack, "--policy", _policy],
            ["build"] = [folder, "--key", _keyPem, "--out", out_ + ".ampack"],
            ["sign"] = [folder, "--key", _keyPem],
            ["verify"] = [pack, "--policy", _policy],
            ["inspect"] = [pack],
            ["diff"] = [pack, other],
            ["lock"] = [folder, pack, "--policy", _policy],
            ["migrate"] = [folder, "--out", out_],
            ["set"] = [out_ + ".ampack", pack, "--id", "io.example.set", "--version", "1.0.0", "--key", _keyPem, "--policy", _policy],
            ["test"] = [pack, "--policy", _policy],
            ["scan"] = [pack],
            ["countersign"] = [pack, "--key", _keyPem, "--role", "assurer", "--out", out_ + ".ampack", "--policy", _policy],
            ["serve"] = [pack, "--policy", _policy],
            ["install"] = [pack, "--store", _store, "--policy", _policy],
            ["receipt"] = ["verify", pack, pack, "--location", "https://example.org/p.ampack", "--policy", _policy],
            ["certify"] = [pack, "--policy", _policy, "--activations", _activations, "--host-key", _pubPem, "--key", _keyPem,
                "--evaluator", "Lab", "--task-source", "tasks", "--out", out_ + ".ampack"],
            ["certificate"] = ["verify", pack, "--policy", _policy],
            ["erasure"] = ["verify", pack, "--policy", _policy],
            ["admit"] = [pack, "--policy", _policy, "--host-signing-key", _keyPem, "--activations", _activations],
            ["gate"] = [pack, "--policy", _policy, "--activations", _activations, "--host-key", _pubPem],
        };
    }

    private IEnumerable<(string Name, Dictionary<string, byte[]> Files)> Corpus()
    {
        const string Id = "io.example.hostile";
        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
        const string Escapes = "\u001b[2J\u001b]0;owned\u0007‮evil⁦x\u009b31m";
        var escaped = System.Text.Json.JsonSerializer.Serialize(Escapes);

        // An unsigned pack whose one record line is 40 MiB of JSON: parsed unbounded it costs several times the budget, so a door that
        // parses before it bounds fails here (review 8, A1: at 8 MiB an unbounded parse fitted under the budget).
        var big = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = Utf8($$"""{"format":"agentmemory.pack/1.2","id":"{{Id}}","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)"}"""),
            ["records/big.jsonl"] = Utf8("{\"text\":\"" + new string('x', 40 << 20) + "\"}\n"),
        };
        big[PackPaths.FilesList] = FilesList.Build(Id, "1.0.0", big);
        yield return ("oversized", big);

        // A signed pack whose strings carry terminal escapes and bidi controls (review 6 M11, review 7 D3).
        var shown = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = Utf8($$$"""{"format":"agentmemory.pack/1.2","id":"{{{Id}}}","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)","display":{"name":{{{escaped}}},"summary":{{{escaped}}}}}"""),
            ["records/notes.jsonl"] = Utf8($$"""{"type":"Note","key":"n1","text":{{escaped}}}""" + "\n"),
        };
        yield return ("escapes", Signed(Id, shown));

        // Review 8, F5: a signed pack of a later minor whose ignored field's name and record text forge whole lines with CR and LF
        // (inspect printed the ignored names raw, so a CR overwrote its line and three forged lines followed).
        const string Forged = "\rFORGED Verified io.example.bank 9.9.9\nFORGED   digest  sha256:0000\r\nFORGED   signed  publisher by trusted";
        var forged = System.Text.Json.JsonSerializer.Serialize(Forged);
        var forgedMinor = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = Utf8($$$"""{"format":"agentmemory.pack/1.3","id":"{{{Id}}}","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)",{{{forged}}}:1}"""),
            ["records/notes.jsonl"] = Utf8($$"""{"type":"Note","key":"n1","text":{{forged}}}""" + "\n"),
        };
        yield return ("forged", Signed(Id, forgedMinor));

        // A signed later minor whose unknown field names carry them (review 7 D3: inspect printed ignored names raw).
        var later = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = Utf8($$"""{"format":"agentmemory.pack/1.3","id":"{{Id}}","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)",{{escaped}}:1}"""),
            ["records/notes.jsonl"] = Utf8("""{"type":"Note","key":"n1","text":"plain"}""" + "\n"),
        };
        yield return ("later-minor", Signed(Id, later));
    }

    // Listed and signed directly, past the writer, which refuses what a hostile writer ships.
    private Dictionary<string, byte[]> Signed(string id, Dictionary<string, byte[]> files)
    {
        files[PackPaths.FilesList] = FilesList.Build(id, "1.0.0", files);
        using var signer = PackSigner.FromPem(_key.ExportPkcs8PrivateKeyPem());
        files[Dsse.PathFor("publisher", signer.KeyId)] = Dsse.Sign(files[PackPaths.FilesList], id, "1.0.0", "publisher", DateTimeOffset.UtcNow.AddMinutes(-5), signer);
        return files;
    }

    private static string PackAuthoringPolicy(ECDsa key) =>
        AgentMemory.Extensibility.Ampack.PackAuthoring.TrustPolicy(key.ExportSubjectPublicKeyInfoPem(), "io.example");

    // The first raw control, C1 or bidi character in the output, named, or a line a pack forged; null when there is none. A CR is
    // raw unless it ends a line (review 8, F5).
    private static string? Raw(string output)
    {
        if (output.Split('\n').FirstOrDefault(line => line.TrimEnd('\r').Contains('\r') || line.StartsWith("FORGED", StringComparison.Ordinal)) is { } bad)
            return bad.StartsWith("FORGED", StringComparison.Ordinal) ? "a forged line" : "U+000D";
        foreach (var c in output)
            if (c is < ' ' and not '\n' and not '\r' and not '\t' || c is >= '\u007f' and <= '\u009f'
                || c is >= '‪' and <= '‮' || c is >= '⁦' and <= '⁩' || c is '‎' or '‏' or '؜')
                return $"U+{(int)c:X4}";
        return null;
    }

    private (int Exit, string Output, long Peak, bool TimedOut) Run(string[] args)
    {
        var start = new ProcessStartInfo(Tool())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, WorkingDirectory = _root,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        long peak = 0;
        var clock = Stopwatch.StartNew();
        var timedOut = false;
        while (!process.WaitForExit(25))
        {
            try { process.Refresh(); peak = Math.Max(peak, process.PeakWorkingSet64); } catch (InvalidOperationException) { break; }
            // A server that started is a door that answered: stopped after it has read its packs.
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) { timedOut = true; process.Kill(entireProcessTree: true); break; }
        }
        process.WaitForExit();
        return (timedOut ? 0 : process.ExitCode, output.Result + error.Result, peak, timedOut);
    }

    private static HashSet<string> CommandTable()
    {
        var assembly = Assembly.LoadFrom(Path.ChangeExtension(Tool(), ".dll"));
        var program = assembly.GetTypes().Single(t => t.GetField("CommandOptions", BindingFlags.NonPublic | BindingFlags.Static) is not null);
        var table = (System.Collections.IDictionary)program.GetField("CommandOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return table.Keys.Cast<string>().ToHashSet(StringComparer.Ordinal);
    }

    private static string Tool()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        var configuration = typeof(PackDoorMatrixTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        return Path.Combine(directory!.FullName, "tools", "Ampack", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "Ampack.exe" : "Ampack");
    }

    private string Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    private string Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Tail(string text) => text.Length > 300 ? text[^300..] : text;
}
