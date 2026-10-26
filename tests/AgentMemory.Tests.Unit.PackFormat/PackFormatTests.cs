using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>Identity, round trips and signatures (v1.1 §1–§3, red-team H1 and H3).</summary>
public sealed class PackFormatTests
{
    [Fact]
    public void A_signed_pack_verifies_as_a_folder_and_as_an_archive_with_one_digest()
    {
        var sample = new SamplePack();
        var files = sample.Signed();

        var asFolder = sample.Verify(files);
        var asArchive = sample.Verify(PackReader.ReadArchive(PackBuilder.Pack(files), "test").Files);

        asFolder.Problems.Should().BeEmpty();
        asArchive.Problems.Should().BeEmpty();
        asArchive.Pack!.Digest.Should().Be(asFolder.Pack!.Digest).And.StartWith("sha256:");
        asFolder.Pack.Signatures.Should().ContainSingle(s => s.Role == "publisher" && s.KeyId == sample.Key.KeyId);
        asFolder.Pack.Files.Keys.Should().BeEquivalentTo(["pack.json", "ontology.json", "records/note.jsonl"]);
    }

    [Fact]
    public void Building_twice_gives_the_same_list_and_the_same_archive_bytes()
    {
        var sample = new SamplePack();
        var first = PackBuilder.Pack(PackBuilder.Build(sample.Source()).Files);
        var second = PackBuilder.Pack(PackBuilder.Build(sample.Source()).Files);
        second.Should().Equal(first);
    }

    [Fact]
    public void The_files_list_is_canonical_and_writes_build_metadata_literally()
    {
        var sample = new SamplePack(version: "1.0.0+build.5");
        var list = PackBuilder.Build(sample.Source()).Files[PackPaths.FilesList];

        Jcs.CheckCanonical(list).Should().BeNull();
        Encoding.UTF8.GetString(list).Should().StartWith("{\"files\":[").And.Contain("\"version\":\"1.0.0+build.5\"");
    }

    [Fact]
    public void A_files_list_that_is_valid_JSON_but_not_canonical_is_refused()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        files[PackPaths.FilesList] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[PackPaths.FilesList]).Replace(",", ", "));

        sample.Verify(files).Problems.Should().ContainSingle().Which.Should().Contain("canonical");
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("extra")]
    [InlineData("missing")]
    public void A_changed_added_or_removed_file_is_refused_before_any_signature_is_read(string change)
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        switch (change)
        {
            case "changed": files["records/note.jsonl"][10] ^= 0x01; break;
            case "extra": files["records/extra.jsonl"] = SamplePack.Utf8("{\"key\":\"x\"}\n"); break;
            case "missing": files.Remove("ontology.json"); break;
        }

        var result = sample.Verify(files);

        result.IsVerified.Should().BeFalse();
        result.Problems.Should().NotContain(p => p.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_rebuilt_list_without_a_new_signature_is_refused()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        files["records/note.jsonl"] = SamplePack.Utf8("{\"key\":\"n1\",\"title\":\"Forged\",\"text\":\"Hello\"}\n");
        var (rebuilt, dropped) = PackBuilder.Build(files);
        dropped.Should().BeTrue("the old signatures no longer sign this content");
        foreach (var (path, bytes) in files.Where(f => PackPaths.IsSignature(f.Key))) rebuilt[path] = bytes;

        sample.Verify(rebuilt).Problems.Should().Contain(p => p.Contains("No valid publisher signature"));
    }

    [Fact]
    public void A_signature_taken_from_another_pack_does_not_count()
    {
        var sample = new SamplePack();
        var victim = sample.Signed();
        var other = sample.Signed(extraRecord: "{\"key\":\"n2\",\"title\":\"Second\",\"text\":\"World\"}\n");
        foreach (var path in victim.Keys.Where(PackPaths.IsSignature).ToList()) victim.Remove(path);
        foreach (var (path, bytes) in other.Where(f => PackPaths.IsSignature(f.Key))) victim[path] = bytes;

        var problems = sample.Verify(victim).Problems;
        problems.Should().Contain(p => p.Contains("No valid publisher signature"));
    }

    [Fact]
    public void A_key_trusted_for_another_namespace_cannot_sign_this_pack()
    {
        var sample = new SamplePack();
        var result = sample.Verify(sample.Signed(), sample.Policy(prefix: "io.other"));
        result.IsVerified.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Contains("No valid publisher signature"));
    }

    [Fact]
    public void A_key_may_sign_only_in_its_roles()
    {
        var sample = new SamplePack();
        var result = sample.Verify(sample.Signed(role: "curator"), sample.Policy());
        result.IsVerified.Should().BeFalse();
    }

    [Fact]
    public void A_namespace_prefix_covers_its_children_but_not_its_lookalikes()
    {
        PackManifest.Covers("io.example", "io.example.notes").Should().BeTrue();
        PackManifest.Covers("io.example", "io.example").Should().BeTrue();
        PackManifest.Covers("io.example", "io.examplex.notes").Should().BeFalse();
    }

    [Fact]
    public void A_policy_whose_key_id_is_not_the_hash_of_its_key_is_refused()
    {
        var key = PackKey.Create();
        var policy = $$"""{"format":"{{TrustPolicy.Format}}","keys":[{"keyId":"{{new string('a', 64)}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(key.PublicKeyPem)}},"idPrefixes":["io.example"],"roles":["publisher"]}]}""";
        var act = () => TrustPolicy.Parse(SamplePack.Utf8(policy));
        act.Should().Throw<InvalidDataException>().WithMessage("*not the SHA-256*");
    }

    [Fact]
    public void A_revoked_digest_a_revoked_key_and_stale_revocations_are_refused()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        var digest = sample.Verify(files).Pack!.Digest;

        sample.Verify(files, sample.Policy() with { RevokedDigests = [digest] }).Problems.Should().Contain(p => p.Contains("revoked"));
        sample.Verify(files, sample.Policy() with { RevokedKeys = [new RevokedKey(sample.Key.KeyId, null)] }).IsVerified.Should().BeFalse();
        sample.Verify(files, sample.Policy() with { RevocationsExpire = SamplePack.Now.AddDays(-1) }).Problems.Should().Contain(p => p.Contains("expired"));
    }

    [Fact]
    public void An_invalid_extra_envelope_does_not_refuse_a_pack_whose_required_signature_holds()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        var stranger = PackKey.Create();
        PackBuilder.Sign(files, stranger.PrivateKeyPem, "curator", SamplePack.SignedAt);
        sample.Verify(files).IsVerified.Should().BeTrue();
    }

    [Fact]
    public void Pack_json_and_the_files_list_must_name_the_same_pack()
    {
        var sample = new SamplePack();
        var source = sample.Source();
        var files = new Dictionary<string, byte[]>(source, StringComparer.Ordinal)
        {
            [PackPaths.FilesList] = FilesList.Build("io.example.other", "1.0.0", source),
        };
        sample.Verify(files).Problems.Should().ContainSingle().Which.Should().Contain("names io.example.notes");
    }
}
