#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Review 6, root RF: the guarantees lived inside the verifier only, so the library's other doors bypassed them. Every load
/// path now authenticates before it parses (H1), the parsers hold their own bounds whoever calls them, and every file a host
/// reads goes through one bounded reader (M2) that checks what it opened (L5).
/// </summary>
public sealed class AuthenticateFirstTests : IDisposable
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly string _root = Directory.CreateTempSubdirectory("authenticate-first-").FullName;
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory")));

    // An archive holding only a pack.json, unsigned: a JSON array of random numbers as large as asked (compressing about 3:1,
    // within the archive profile, as the reviewer's did).
    private static byte[] Unsigned(int bytes)
    {
        var rng = new Random(6);
        var json = new StringBuilder("[", bytes);
        while (json.Length < bytes - 12) json.Append(rng.Next(1_000_000)).Append(',');
        json.Append("1]");
        return StrictZip.Write(new Dictionary<string, byte[]> { ["pack.json"] = Encoding.UTF8.GetBytes(json.ToString()) });
    }

    private static IReadOnlyList<string> Refusal(Action load) =>
        FluentActions.Invoking(load).Should().Throw<PackLoadException>().Which.Problems;

    [Fact]
    public void H1_every_load_path_refuses_an_unsigned_pack_before_it_reads_pack_json()
    {
        // The reviewer's archive: test, install and serve answered "pack.json is not an object", having parsed 16 MiB unsigned.
        var archive = Unsigned(4 << 20);
        var trust = Trust();

        Refusal(() => PackLoader.Peek(archive, trust)).Should().ContainSingle().Which.Should().Contain("META/files.json");
        Refusal(() => PackMemory.Mount([archive], trust)).Should().ContainSingle().Which.Should().Contain("META/files.json");
        Refusal(() => PackStore.Open(_root).Install(archive, trust)).Should().ContainSingle().Which.Should().Contain("META/files.json");
        var inspection = PackInspection.Inspect(archive, trust);
        inspection.Problems.Should().Contain(p => p.Contains("META/files.json"));
        inspection.Kind.Should().BeNull("nothing but the signed list is read before the signatures hold");
        inspection.Declarations.Should().BeEmpty();
    }

    [Fact]
    public void Review8_A12_a_pack_whose_signature_does_not_hold_is_refused_for_that_alone_at_every_load_path()
    {
        // The unsigned probe above is refused at its list, before any envelope; this one gets as far as the signatures: a valid list
        // and a well-formed envelope by a key the host does not trust, over a pack.json and a record line that fail if parsed. Every
        // path refuses it for the signature, and nothing else is said about it, so nothing past the signatures was read.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = "[\"not a manifest\"]"u8.ToArray(),
            ["records/notes.jsonl"] = "{\"unterminated\": \n"u8.ToArray(),
        };
        files[PackPaths.FilesList] = FilesList.Build("io.agentmemory.packs.probe", "1.0.0", files);
        using (var stranger = PackSigner.FromPem(PackAuthoring.CreateKey().PrivateKeyPem))
            files[Dsse.PathFor("publisher", stranger.KeyId)] = Dsse.Sign(files[PackPaths.FilesList], "io.agentmemory.packs.probe", "1.0.0", "publisher",
                DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()), stranger);
        var archive = StrictZip.Write(files);
        var trust = Trust();
        static void OnlyTheSignature(IReadOnlyList<string> problems)
        {
            problems.Should().Contain(p => p.Contains("No valid publisher signature"));
            problems.Should().NotContain(p => p.Contains("pack.json") || p.Contains("records/") || p.Contains("line "));
        }
        // Counted, not inferred from the message (review 9, D7): no pack.json is parsed on any path before the signatures hold.
        IReadOnlyList<string> Unparsed(Action load)
        {
            var before = PackManifest.ParsedOnThisThread;
            var problems = Refusal(load);
            PackManifest.ParsedOnThisThread.Should().Be(before, "nothing past the signatures is parsed");
            return problems;
        }

        OnlyTheSignature(Unparsed(() => PackLoader.Peek(archive, trust)));
        OnlyTheSignature(Unparsed(() => PackLoader.Load(archive, trust)));
        OnlyTheSignature(Unparsed(() => PackMemory.Mount([archive], trust)));
        OnlyTheSignature(Unparsed(() => PackStore.Open(_root).Install(archive, trust)));
        var before = PackManifest.ParsedOnThisThread;
        var inspection = PackInspection.Inspect(archive, trust);
        PackManifest.ParsedOnThisThread.Should().Be(before);
        OnlyTheSignature(inspection.Problems);
        inspection.Kind.Should().BeNull();
        inspection.Declarations.Should().BeEmpty();
    }

    [Fact]
    public void H1_a_signed_pack_still_loads_through_every_path()
    {
        var source = PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal));
        var archive = PackAuthoring.Build(source, _key.PrivateKeyPem);

        PackLoader.Peek(archive, Trust()).Id.Should().Be("io.agentmemory.packs.martialarts");
        PackMemory.Mount([archive], Trust()).Packs.Should().ContainSingle();
        PackStore.Open(_root).Install(archive, Trust()).Id.Should().Be("io.agentmemory.packs.martialarts");
        PackInspection.Inspect(archive, Trust()).Declarations.Should().ContainKey("pack.json");
        // Signed by a key the host does not trust: refused for that, before its pack.json is read.
        var stranger = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(PackAuthoring.CreateKey().PublicKeyPem, "io.agentmemory")));
        Refusal(() => PackLoader.Peek(archive, stranger)).Should().Contain(p => p.Contains("No valid publisher signature"));
    }

    [Fact]
    public void The_parsers_hold_their_own_bounds_whoever_calls_them()
    {
        FilesList.Parse(new byte[(2 << 20) + 1]).Problem.Should().Contain("larger than 2 MiB");
        PackManifest.Parse(new byte[(1 << 20) + 1]).Problem.Should().Contain("larger than 1 MiB");
        PackCard.Parse(new byte[(1 << 20) + 1]).Problem.Should().Contain("larger than 1 MiB");
        LockEntryParse(new byte[(1 << 20) + 1]).Should().Contain("larger than 1 MiB");
        FluentActions.Invoking(() => PackTrust.Parse(new byte[(4 << 20) + 1])).Should().Throw<InvalidDataException>().WithMessage("*larger than 4194304*");
    }

    private static string? LockEntryParse(byte[] bytes) => PackLock.Parse(bytes).Problem;

    [Fact]
    public void L4_a_listed_hash_is_64_hex_digits_and_nothing_after()
    {
        var source = PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal));
        var files = StrictZip.Read(PackAuthoring.Build(source, _key.PrivateKeyPem), PackLimits.Default);
        var list = JsonNode.Parse(files["META/files.json"])!.AsObject();
        var first = list["files"]!.AsArray()[0]!.AsObject();
        first["sha256"] = first["sha256"]!.GetValue<string>() + "\n";

        FilesList.Parse(Jcs.Canonical(list)).Problem.Should().NotBeNull("$ matched before a final newline");
    }

    [Fact]
    public void M2_a_host_reads_its_policy_bounded()
    {
        // The reviewer's policy: valid, padded with spaces to 5 MiB. ampack verify refused it; ampack test loaded it.
        var policy = Path.Combine(_root, "policy.json");
        var valid = PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory");
        File.WriteAllText(policy, valid.TrimEnd().TrimEnd('}') + new string(' ', 5 << 20) + "}");
        FluentActions.Invoking(() => PackTrust.Load(policy)).Should().Throw<InvalidDataException>().WithMessage("*larger than 4194304*");
        FluentActions.Invoking(() => PackTrust.Load(_root)).Should().Throw<InvalidDataException>().WithMessage("*is a folder*");
        File.WriteAllText(policy, valid);
        PackTrust.Load(policy).Should().NotBeNull();
    }

    [Fact]
    public void M2_an_activation_record_over_its_bound_is_not_a_record()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "activations")).FullName;
        File.WriteAllText(Path.Combine(folder, "big.dsse.json"), "{\"payload\":\"" + new string('A', (1 << 20) + 16) + "\"}");
        using var host = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        new PackActivationFolder(folder, host.ExportSubjectPublicKeyInfoPem()).Ignored.Should().Equal("big.dsse.json");
    }
}
