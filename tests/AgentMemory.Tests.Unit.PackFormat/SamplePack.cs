using System.Text;
using AgentMemory.Packs.Format;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>A small, valid pack, its signing key, and a policy that trusts that key for its namespace.</summary>
internal sealed class SamplePack
{
    internal static readonly DateTimeOffset SignedAt = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset Now = SignedAt.AddHours(1);

    internal PackKey Key { get; } = PackKey.Create();
    internal string Id { get; }
    internal string Version { get; }

    internal SamplePack(string id = "io.example.notes", string version = "1.0.0")
    {
        Id = id;
        Version = version;
    }

    internal Dictionary<string, byte[]> Source(string? extraRecord = null) => new(StringComparer.Ordinal)
    {
        ["pack.json"] = Utf8($$"""{"format":"agentmemory.pack/1.2","id":"{{Id}}","version":"{{Version}}","kind":"knowledge","sdk":"[0.1,0.2)","tier":"open","licence":"MIT"}"""),
        ["ontology.json"] = Utf8("""{"types":[{"name":"Note","properties":["title","text"]}]}"""),
        ["records/note.jsonl"] = Utf8("{\"key\":\"n1\",\"title\":\"First\",\"text\":\"Hello\"}\n" + (extraRecord ?? "")),
    };

    /// <summary>Built and signed by <see cref="Key"/> as publisher.</summary>
    internal Dictionary<string, byte[]> Signed(string? extraRecord = null, string role = "publisher")
    {
        var (files, _) = PackBuilder.Build(Source(extraRecord));
        PackBuilder.Sign(files, Key.PrivateKeyPem, role, SignedAt);
        return files;
    }

    internal TrustPolicy Policy(string? prefix = null, IReadOnlyList<string>? roles = null) => new()
    {
        Keys = [new TrustedKey(Key.KeyId, Key.PublicKeyPem, [prefix ?? "io.example"], roles ?? ["publisher"], null, null)],
    };

    internal PackVerification Verify(IReadOnlyDictionary<string, byte[]> files, TrustPolicy? policy = null, HostLedger? ledger = null) =>
        PackVerifier.Verify(new PackContent(files, "test"), policy ?? Policy(), Now, ledger);

    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// Signs the list as it stands, past every rule the writer holds: what a hostile or later writer ships, which only a
    /// reader's checks may answer for (review 7, F2: <see cref="PackBuilder"/> refuses to sign such a pack).
    /// </summary>
    internal static void SignRaw(Dictionary<string, byte[]> files, string privateKeyPem, string role, DateTimeOffset signedAt)
    {
        var (list, problem) = FilesList.Parse(files[PackPaths.FilesList]);
        if (list is null) throw new InvalidDataException(problem);
        using var signer = PackSigner.FromPem(privateKeyPem);
        files[Dsse.PathFor(role, signer.KeyId)] = Dsse.Sign(files[PackPaths.FilesList], list.Id, list.Version, role, signedAt, signer);
    }
}
