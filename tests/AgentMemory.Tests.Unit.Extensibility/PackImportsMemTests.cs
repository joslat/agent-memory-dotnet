#pragma warning disable AMEXT001
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Feature 15, R1-R3: a .mem bundle verifies whole before anything imports, and its tombstones become erasures.</summary>
public sealed class PackImportsMemTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();
    private static readonly string PublicKey = new('a', 64);

    // A stand-in for Ed25519: a signature verifies when it is the message's SHA-512, under the one key above.
    private static bool Fake(byte[] key, byte[] signature, byte[] message) =>
        Convert.ToHexStringLower(key) == PublicKey && signature.AsSpan().SequenceEqual(SHA512.HashData(message));

    private static string Token(byte[] message) => $"ed25519:{PublicKey}:{Convert.ToHexStringLower(SHA512.HashData(message))}";

    private static Dictionary<string, byte[]> Bundle(bool signed = false, Dictionary<string, string>? with = null)
    {
        var items = with ?? new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["items/episode.jsonl"] = """{"id":"ep1","details":"Kept episode.","eventTime":"2026-01-02T03:04:05Z"}""" + "\n"
                + """{"id":"ep2","details":"Deleted episode."}""" + "\n",
            ["audit/tombstones.jsonl"] = """{"op":"delete","targetID":"ep2","deletedAt":"2026-02-01T00:00:00Z"}""" + "\n",
        };
        var files = items.ToDictionary(i => i.Key, i => Encoding.UTF8.GetBytes(i.Value), StringComparer.Ordinal);
        var sums = Encoding.UTF8.GetBytes(string.Concat(files.Keys.Order(StringComparer.Ordinal)
            .Select(p => $"{Convert.ToHexStringLower(SHA256.HashData(files[p]))}  {p}\n")));
        var manifest = new JsonObject
        {
            ["format"] = "mem/1.1",
            ["files"] = new JsonArray([.. files.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.Key, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(f.Value)), ["bytes"] = f.Value.Length,
            })]),
            ["bundleDigest"] = Convert.ToHexStringLower(SHA256.HashData(sums)),
        };
        files["CHECKSUMS"] = sums;
        files["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        if (signed) files["manifest.sig"] = Encoding.UTF8.GetBytes(Token(files["manifest.json"]));
        return files;
    }

    private static ImportResult Import(Dictionary<string, byte[]> files, IReadOnlySet<string>? trusted = null) =>
        PackImports.FromMem(files, Fake, trusted, "com.example.mem", "1.0.0", "Example Owner", Key.PrivateKeyPem);

    private static string Derivation(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive));
        using var reader = new StreamReader(zip.GetEntry("META/derivation.json")!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void A_bundle_imports_what_its_tombstones_leave_and_carries_them_as_erasures()
    {
        var result = Import(Bundle());
        result.Records.Should().Be(1);
        result.Skipped.Should().ContainSingle(s => s.Contains("ep2") && s.Contains("tombstone"));
        using var zip = new ZipArchive(new MemoryStream(result.Archive));
        zip.Entries.Should().ContainSingle(e => e.FullName.StartsWith("META/attestations/") && e.FullName.EndsWith(".erasure.dsse.json"));
        Derivation(result.Archive).Should().Contain("\"sourceSignature\":\"absent\"");
    }

    [Fact]
    public void A_bundle_that_does_not_verify_whole_imports_nothing()
    {
        var tampered = Bundle();
        tampered["items/episode.jsonl"] = Encoding.UTF8.GetBytes("""{"id":"ep1","details":"Changed."}""" + "\n");
        FluentActions.Invoking(() => Import(tampered)).Should().Throw<InvalidDataException>().WithMessage("*digest or size*");

        var stray = Bundle();
        stray["items/extra.jsonl"] = [];
        FluentActions.Invoking(() => Import(stray)).Should().Throw<InvalidDataException>().WithMessage("*not in its manifest*");

        var badSig = Bundle(signed: true);
        badSig["manifest.sig"] = Encoding.UTF8.GetBytes($"ed25519:{PublicKey}:{new string('0', 128)}");
        FluentActions.Invoking(() => Import(badSig)).Should().Throw<InvalidDataException>().WithMessage("*does not verify*");
    }

    [Fact]
    public void An_export_is_canonical_reproducible_and_imports_back_under_its_signature()
    {
        var pack = PackLoader.Load(Import(Bundle()).Archive, PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example"))));
        var at = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var first = PackExports.ToMem(pack, at, "test/1", m => (Convert.FromHexString(PublicKey), SHA512.HashData(m)));
        var second = PackExports.ToMem(pack, at, "test/1", m => (Convert.FromHexString(PublicKey), SHA512.HashData(m)));
        first.Files.Keys.Should().BeEquivalentTo(second.Files.Keys);
        foreach (var (path, bytes) in first.Files) bytes.Should().Equal(second.Files[path], $"{path} is the same bytes each time");
        first.Episodes.Should().Be(1);
        first.Notes.Should().ContainSingle(n => n.Contains("salted hash"), "the import's erasure names its record by hash, which a tombstone cannot carry");

        var manifest = Encoding.UTF8.GetString(first.Files["manifest.json"]);
        manifest.Should().NotContain(" ").And.StartWith("{\"bundleDigest\":");
        Encoding.UTF8.GetString(first.Files["items/episode.jsonl"]).Should().EndWith("}\n").And.Contain("\"details\":\"Kept episode.\"").And.Contain("\"importance\":0.5");

        var back = Import(new Dictionary<string, byte[]>(first.Files, StringComparer.Ordinal), new HashSet<string> { PublicKey });
        back.Records.Should().Be(1);
        Derivation(back.Archive).Should().Contain("(trusted)");
    }

    [Fact]
    public void Review_15_17_a_tombstoned_preference_is_not_imported_and_a_tombstone_is_verified_only_under_a_trusted_key()
    {
        // Keys in sorted order and plain string values, so this text is the tombstone's canonical form, which its signature covers.
        var tombstone = new JsonObject { ["deletedAt"] = "2026-02-01T00:00:00Z", ["op"] = "delete", ["targetID"] = "theme", ["targetKind"] = "preference" };
        tombstone["signature"] = Token(Encoding.UTF8.GetBytes(tombstone.ToJsonString()));
        var items = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["items/preference.jsonl"] = """{"key":"theme","value":"dark"}""" + "\n" + """{"key":"lang","value":"en"}""" + "\n",
            ["audit/tombstones.jsonl"] = tombstone.ToJsonString() + "\n",
        };
        var result = Import(Bundle(with: items));
        result.Records.Should().Be(1, "the tombstone names the preference by its key, and its note is preference/theme");
        result.Skipped.Should().Contain(s => s.Contains("preference/theme") && s.Contains("tombstone"));
        Derivation(result.Archive).Should().Contain("0 verified under a trusted key, 1 valid under a key not trusted here");
        Derivation(Import(Bundle(signed: true, with: items), new HashSet<string> { PublicKey }).Archive).Should().Contain("1 verified under a trusted key");
    }

    [Fact]
    public void A_signature_is_recorded_as_trusted_only_under_a_trusted_key_and_trust_requires_one()
    {
        Derivation(Import(Bundle(signed: true)).Archive).Should().Contain("not trusted here");
        Derivation(Import(Bundle(signed: true), new HashSet<string> { PublicKey }).Archive).Should().Contain("(trusted)");
        FluentActions.Invoking(() => Import(Bundle(signed: true), new HashSet<string> { new('b', 64) })).Should().Throw<InvalidDataException>().WithMessage("*not trusted*");
        FluentActions.Invoking(() => Import(Bundle(), new HashSet<string> { PublicKey })).Should().Throw<InvalidDataException>().WithMessage("*no manifest.sig*");
    }
}
