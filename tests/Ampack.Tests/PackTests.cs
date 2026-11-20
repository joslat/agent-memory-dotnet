using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace Ampack.Tests;

public sealed class PackTests
{
    [Fact]
    public void Every_plain_pack_vector_reaches_its_recorded_verdict_revocations_included()
    {
        var (plain, missing, total) = Kit.Cases();
        Assert.True(plain.Count > 300, $"most of the {total} vectors are plain packs; found {plain.Count}");
        Assert.Equal(["policy-with-a-private-key"], missing);
        var wrong = new List<string>();
        foreach (var c in plain)
        {
            var options = new PackOptions { Revocations = [.. c.Revocations.Select(name => (ReadOnlyMemory<byte>)Kit.Trust(name))] };
            var verdict = Pack.Verify(Kit.Vector(c.Vector), Kit.Trust(c.Policy), options).Verified ? "verified" : "refused";
            if (verdict != c.Expected) wrong.Add($"{c.Vector}: expected {c.Expected}, got {verdict}");
        }
        Assert.Empty(wrong);
    }

    [Fact]
    public void A_knowledge_pack_reads()
    {
        var pack = Pack.Load(Kit.Vector("valid-python-built"), Kit.Trust());
        Assert.Equal(("io.example.notes", "1.0.0", "knowledge"), (pack.Id, pack.Version, pack.Kind));
        Assert.Matches("^sha256:[0-9a-f]{64}$", pack.Digest);
        Assert.Equal(["core"], pack.Profiles);
        Assert.Equal("Notes", pack.Title);
        var verdict = Pack.Verify(Kit.Vector("valid-python-built"), Kit.Trust());
        Assert.True(verdict.Verified);
        Assert.Equal((pack.Id, pack.Version, pack.Digest, pack.Kind), (verdict.Id, verdict.Version, verdict.Digest, verdict.Kind));
        var records = pack.Records();
        Assert.NotEmpty(records);
        Assert.Equal(1, records[0].Line);
        Assert.Contains(records[0].File, pack.RecordFiles);
        using var manifest = pack.Manifest;
        Assert.Equal(pack.Id, manifest.RootElement.GetProperty("id").GetString());
        Assert.True(pack.Contains("pack.json"));
        Assert.Null(pack.Bytes("no/such/file"));
    }

    [Fact]
    public void The_files_are_the_ones_the_digest_covers()
    {
        // This vector verifies and carries a certificate nobody checked: it must not come out as a file of the verified pack.
        var pack = Pack.Load(Kit.Vector("attestation-certificate-content-unchecked"), Kit.Trust());
        Assert.True(pack.Contains("pack.json"));
        Assert.DoesNotContain(pack.Paths, p => p.StartsWith("META/", StringComparison.Ordinal));
        var (plain, _, _) = Kit.Cases();
        foreach (var c in plain.Where(c => c.Expected == "verified" && c.Policy == "trust.json" && c.Revocations.Count == 0))
        {
            var loaded = Pack.Load(Kit.Vector(c.Vector), Kit.Trust());
            foreach (var each in loaded.Members.Prepend(loaded))
                Assert.DoesNotContain(each.Paths, p => p == "META/files.json" || p.StartsWith("META/signatures/", StringComparison.Ordinal)
                    || p.StartsWith("META/attestations/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_file_paths_are_taken_as_well_as_bytes()
    {
        var pack = Pack.Load(Path.Combine(Kit.Root, "vectors", "valid-python-built.ampack"), Path.Combine(Kit.Root, "trust.json"));
        Assert.Equal("io.example.notes", pack.Id);
        Assert.False(Pack.Verify(Path.Combine(Kit.Root, "vectors", "no-such.ampack"), Path.Combine(Kit.Root, "trust.json")).Verified);
        Assert.Throws<PackRefusedException>(() => Pack.Load(Path.Combine(Kit.Root, "vectors", "no-such.ampack"), Path.Combine(Kit.Root, "trust.json")));
    }

    [Fact]
    public void A_set_gives_its_members_each_with_its_digest()
    {
        var (plain, _, _) = Kit.Cases();
        var set = plain.Where(c => c.Expected == "verified" && c.Policy == "trust.json" && c.Revocations.Count == 0)
            .Select(c => Pack.Load(Kit.Vector(c.Vector), Kit.Trust())).First(p => p.Kind == "set");
        Assert.Empty(set.Records());
        Assert.NotEmpty(set.Members);
        using var manifest = set.Manifest;
        Assert.Equal(manifest.RootElement.GetProperty("contains").EnumerateArray().Select(m => m.GetString()).Order(StringComparer.Ordinal),
            set.Members.Select(m => m.Id));
        Assert.All(set.Members, m => { Assert.NotEqual("set", m.Kind); Assert.Matches("^sha256:[0-9a-f]{64}$", m.Digest); });
    }

    [Fact]
    public void Nothing_comes_out_of_a_refused_pack()
    {
        var refused = Assert.Throws<PackRefusedException>(() => Pack.Load(Kit.Vector("tampered-record"), Kit.Trust()));
        Assert.Contains("does not match META/files.json", refused.Message);
        var verdict = Pack.Verify(Kit.Vector("tampered-record"), Kit.Trust());
        Assert.False(verdict.Verified);
        Assert.Null(verdict.Id);
        Assert.NotEmpty(verdict.Reasons);
    }

    [Fact]
    public void Inspect_describes_an_archive_and_judges_nothing()
    {
        var summary = Pack.Inspect(Kit.Vector("tampered-record"));
        Assert.Equal(("io.example.notes", "1.0.0", "knowledge", 1), (summary.Id, summary.Version, summary.Kind, summary.Envelopes));
        Assert.True(summary.Files > 0 && summary.Bytes > 0);
        Assert.Throws<PackRefusedException>(() => Pack.Inspect(new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("[1]", "a record is a JSON object")]
    [InlineData("""{"type":"T"}""", "needs a string type and key")]
    [InlineData("""{"type":"T","key":1}""", "needs a string type and key")]
    [InlineData("""{"type":"T","key":"k","properties":[]}""", "properties is an object")]
    [InlineData("""{"type":"T","key":"k","links":[{"relation":"r"}]}""", "links is a list")]
    [InlineData("""{"type":"T","key":"k","links":[{"relation":"r","target":"t","more":1}]}""", "links is a list")]
    [InlineData("""{"type":"T","key":"k","colour":"red"}""", "unknown field colour")]
    // Nine levels: the line, properties, and seven objects inside. A knowledge pack's record nests at most eight.
    [InlineData("""{"type":"T","key":"k","properties":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":1}}}}}}}}}""", "nesting")]
    public void A_malformed_line_is_refused(string line, string problem)
    {
        var refused = Assert.Throws<PackRefusedException>(() => Pack.Record(Encoding.UTF8.GetBytes(line), "records/x.jsonl", 3, "records/x.jsonl:3", knowledge: true));
        Assert.Contains(problem, refused.Message);
        Assert.StartsWith("records/x.jsonl:3: ", refused.Message);
    }

    [Fact]
    public void A_record_may_nest_eight_levels_in_a_knowledge_pack_and_deeper_elsewhere()
    {
        var eight = """{"type":"T","key":"k","properties":{"a":{"b":{"c":{"d":{"e":{"f":{"g":1}}}}}}}}"""u8;
        Assert.Equal("k", Pack.Record(eight, "records/x.jsonl", 1, "records/x.jsonl:1", knowledge: true).Key);
        var nine = """{"type":"T","key":"k","properties":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":1}}}}}}}}}"""u8;
        Assert.Equal("k", Pack.Record(nine, "records/x.jsonl", 1, "records/x.jsonl:1", knowledge: false).Key);
    }

    [Fact]
    public void A_line_keeps_the_formats_other_fields()
    {
        var record = Pack.Record("""{"type":"T","key":"k","validFrom":"2026-01-01T00:00:00Z","confidenceMicro":900000,"links":[{"relation":"r","target":"t"}]}"""u8,
            "records/x.jsonl", 1, "records/x.jsonl:1", knowledge: true);
        Assert.Equal(["confidenceMicro", "validFrom"], record.Extra.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(record.Properties);
        Assert.Equal([new PackLink("r", "t")], record.Links);
    }

    [Fact]
    public void The_public_surface_is_the_Ampack_namespace_and_nothing_else()
    {
        var exported = typeof(Pack).Assembly.GetExportedTypes();
        Assert.All(exported, t => Assert.Equal("Ampack", t.Namespace));
        Assert.Equal(["Pack", "PackLink", "PackOptions", "PackRecord", "PackRefusedException", "PackSummary", "PackVerdict"],
            exported.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_assembly_depends_on_dotnet_alone()
    {
        var references = typeof(Pack).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.All(references, name => Assert.True(name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard" or "mscorlib",
            $"{name} is not part of .NET"));
    }

    [Fact]
    public void The_assembly_holds_no_signing()
    {
        // No builder, no key pair made here, no function that signs a pack, a receipt or a revocation list: by type and method
        // name, the internal ones included.
        var assembly = typeof(Pack).Assembly;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var types = assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.Name == "PackBuilder");
        var methods = types.SelectMany(t => t.GetMethods(all).Select(m => $"{t.Name}.{m.Name}")).ToList();
        Assert.DoesNotContain(methods, m => m is "PackKey.Create" or "Dsse.Sign" or "Revocations.Sign" or "PublicationReceipt.Sign"
            || m.EndsWith(".SignData", StringComparison.Ordinal) || m.EndsWith(".Countersign", StringComparison.Ordinal) || m.EndsWith(".Issue", StringComparison.Ordinal));

        // The signer is a stand-in that cannot exist: sealed, one private constructor, no key. Three functions of the format
        // library take one (certificate, aggregate, erasure) and are therefore unusable here.
        var signer = assembly.GetType("AgentMemory.Packs.Format.PackSigner", throwOnError: true)!;
        Assert.True(signer.IsSealed);
        Assert.All(signer.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), c => Assert.True(c.IsPrivate));
        Assert.Empty(signer.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));   // it holds nothing: no key
        Assert.DoesNotContain(types, t => t.BaseType == signer);

        // And it calls nothing of .NET that signs or handles a private key: read from the assembly's own member references.
        using var file = File.OpenRead(assembly.Location);
        using var portable = new PEReader(file);
        var metadata = portable.GetMetadataReader();
        var called = metadata.MemberReferences.Select(h => metadata.GetString(metadata.GetMemberReference(h).Name)).ToHashSet(StringComparer.Ordinal);
        string[] forbidden = ["SignData", "SignHash", "TrySignData", "TrySignHash", "ExportPkcs8PrivateKey", "ExportPkcs8PrivateKeyPem", "ExportECPrivateKey",
            "ExportECPrivateKeyPem", "ExportEncryptedPkcs8PrivateKey", "ExportEncryptedPkcs8PrivateKeyPem", "ImportPkcs8PrivateKey", "ImportECPrivateKey",
            "ImportEncryptedPkcs8PrivateKey", "ImportFromEncryptedPem", "GenerateKey"];
        Assert.Equal([], forbidden.Where(called.Contains).ToArray());
        // The check itself sees real calls: verification is there.
        Assert.Contains("VerifyHash", called);
    }
}
