#pragma warning disable AMEXT001
using System.Buffers.Binary;
using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 03, the Embeddings profile: vectors are signed content a host reuses only on an exact match of the model that made
/// them (R-EMB-008), and that never reach the walk or a verdict (R-EMB-009). The layout itself is the kit's (both verifiers).
/// </summary>
public sealed class PackEmbeddingsTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();

    private static readonly PackEmbeddings.Index Set = new("notes", "example-embedder-4", "sha256:abc", "F32", 4, true, "cosine", [("note", "n1")]);

    [Fact]
    public void Vectors_are_reused_only_on_an_exact_match_of_the_model_that_made_them()
    {
        PackEmbeddings.Reusable(Set, new("example-embedder-4", "sha256:abc", 4, "F32", true)).Should().BeTrue();
        foreach (var other in new PackEmbeddings.Embedder[]
                 {
                     new("example-embedder-5", "sha256:abc", 4, "F32", true), new("example-embedder-4", "sha256:abd", 4, "F32", true),
                     new("example-embedder-4", null, 4, "F32", true), new("example-embedder-4", "sha256:abc", 8, "F32", true),
                     new("example-embedder-4", "sha256:abc", 4, "F16", true), new("example-embedder-4", "sha256:abc", 4, "F32", false),
                 })
            PackEmbeddings.Reusable(Set, other).Should().BeFalse($"{other} differs from the set's model in one respect");
        // Review F03, M4: a pack that gives no digest matches only a host that knows none.
        var undigested = Set with { ModelDigest = null };
        PackEmbeddings.Reusable(undigested, new("example-embedder-4", null, 4, "F32", true)).Should().BeTrue();
        PackEmbeddings.Reusable(undigested, new("example-embedder-4", "sha256:abc", 4, "F32", true)).Should().BeFalse();
    }

    private static byte[] Tensor(int rows, int dim)
    {
        var header = Encoding.UTF8.GetBytes("""{"vectors":{"dtype":"F32","shape":[""" + rows + "," + dim + """],"data_offsets":[0,""" + rows * dim * 4 + "]}}");
        var bytes = new byte[8 + header.Length + rows * dim * 4];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)header.Length);
        header.CopyTo(bytes, 8);
        for (var i = 0; i < rows * dim; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8 + header.Length + i * 4), 0.5f);
        return bytes;
    }

    private static LoadedPack Load(bool vectors)
    {
        var module = """{"format":"agentmemory.module/v1alpha1","id":"com.example.lantern-notes","version":"1.0.0","sdk":"[0.1,0.2)","seed":["records/notes.jsonl"]}""";
        var bundle = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(module),
            ["records/notes.jsonl"] = Encoding.UTF8.GetBytes("""{"type":"note","key":"n1","properties":{"text":"The lantern is lit at dusk."}}""" + "\n"),
        };
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(bundle), StringComparer.Ordinal);
        if (vectors)
        {
            source["embeddings/notes.st"] = Tensor(1, 4);
            source["embeddings/notes.json"] = Encoding.UTF8.GetBytes(
                """{"format":"ampack.embeddings/1","model":"example-embedder-4","dtype":"F32","dimension":4,"normalized":false,"distance":"cosine","rows":[["note","n1"]]}""");
        }
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example")));
        return PackLoader.Load(PackAuthoring.Build(source, Key.PrivateKeyPem), trust);
    }

    [Fact]
    public void A_pack_with_vectors_composes_the_same_module_and_records_as_without_so_the_walk_cannot_see_them()
    {
        var with = Load(vectors: true);
        var without = Load(vectors: false);

        with.Digest.Should().NotBe(without.Digest, "the vectors are signed content");
        AgentMemory.Extensibility.Modules.PackTrialGate.DigestOf(with.Manifest).Should().Be(AgentMemory.Extensibility.Modules.PackTrialGate.DigestOf(without.Manifest),
            "the declarations a walk and a trial read are the same");
        with.Knowledge!.Proposals.Select(p => (p.Type, p.Key)).Should().Equal(without.Knowledge!.Proposals.Select(p => (p.Type, p.Key)));
    }
}
