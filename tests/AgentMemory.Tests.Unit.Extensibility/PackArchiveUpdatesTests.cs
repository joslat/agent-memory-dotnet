using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class PackArchiveUpdatesTests
{
    static byte[] Archive(PackSigningKey key, string text = "published") => PackAuthoring.Build(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>
    {
        ["manifest.json"] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.module/v1alpha1","id":"io.test.notes","version":"1.0.0","sdk":"[0.1,0.2)","seed":["seed/notes.jsonl"],"ontology":{"labelPrefix":"Note","types":[{"name":"NoteItem","scope":"shared","properties":{"text":{"type":"string"}}}]}}"""),
        ["seed/notes.jsonl"] = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { type = "NoteItem", key = "one", properties = new { text } }) + "\n"),
    }), key.PrivateKeyPem);
    static byte[] Attested(byte[] archive, PackSigningKey key, string record, DateTimeOffset issued)
    {
        using var signer = PackSigner.FromPem(key.PrivateKeyPem);
        var files = PackReader.ReadArchive(archive, "fixture").Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        files[PackErasure.PathFor(key.KeyId, issued)] = PackErasure.Create("io.test.notes", [("NoteItem", record)], "withdrawn", issued, signer);
        return PackBuilder.Pack(files);
    }

    [Fact]
    public void Evidence_merges_monotonically_without_changing_signed_content()
    {
        var key = PackAuthoring.CreateKey(); var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "io.test")));
        var original = Archive(key); var a = Attested(original, key, "one", DateTimeOffset.UtcNow.AddMinutes(-2));
        var b = Attested(original, key, "two", DateTimeOffset.UtcNow.AddMinutes(-1));
        var merged = PackArchiveUpdates.MergeAttestations(a, b, trust);
        merged = PackArchiveUpdates.MergeAttestations(merged, original, trust);
        Assert.Equal(PackLoader.Load(original, trust).Digest, PackLoader.Load(merged, trust).Digest);
        Assert.Equal(2, PackLoader.Load(merged, trust).Erasures.Count);
    }

    [Fact]
    public void Two_valid_but_conflicting_notices_at_the_same_path_are_refused()
    {
        var key = PackAuthoring.CreateKey(); var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "io.test")));
        var original = Archive(key); var issued = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Throws<PackLoadException>(() => PackArchiveUpdates.MergeAttestations(Attested(original, key, "one", issued), Attested(original, key, "two", issued), trust));
    }

    [Fact]
    public void Changed_content_is_refused_even_with_a_trusted_signer()
    {
        var key = PackAuthoring.CreateKey(); var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "io.test")));
        Assert.Throws<PackLoadException>(() => PackArchiveUpdates.MergeAttestations(Archive(key), Archive(key, "different"), trust));
    }

    [Fact]
    public void An_untrusted_incoming_copy_is_refused_before_evidence_is_used()
    {
        var key = PackAuthoring.CreateKey(); var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "io.test")));
        Assert.Throws<PackLoadException>(() => PackArchiveUpdates.MergeAttestations(Archive(key), Archive(PackAuthoring.CreateKey()), trust));
    }
}
