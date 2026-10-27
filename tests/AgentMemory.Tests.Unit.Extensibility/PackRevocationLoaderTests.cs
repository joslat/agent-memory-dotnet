using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>A host applies a signed revocation document to its trust (PACK-harden): the pack it names no longer loads.</summary>
public sealed class PackRevocationLoaderTests
{
    [Fact]
    public void A_host_with_a_revocation_document_refuses_the_pack_it_names()
    {
        var publisher = PackAuthoring.CreateKey();
        var revoker = PackKey.Create();
        var policy = JsonDocument.Parse(PackAuthoring.TrustPolicy(publisher.PublicKeyPem, "io.example")).RootElement;
        var keys = policy.GetProperty("keys").EnumerateArray().Select(k => (object)k).Append(new
        {
            keyId = revoker.KeyId, publicKey = revoker.PublicKeyPem, idPrefixes = new[] { "io.example" }, roles = new[] { "revoker" },
        });
        var trust = PackTrust.Parse(JsonSerializer.SerializeToUtf8Bytes(new { format = "agentmemory.pack.trust/1.1", keys }));
        var archive = PackAuthoring.Build(new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.module/v1alpha1","id":"io.example.notes","version":"1.0.0","sdk":"[0.1,0.2)"}"""),
        }), StringComparer.Ordinal), publisher.PrivateKeyPem);
        var pack = PackLoader.Load(archive, trust);

        var revoked = trust.WithRevocations(Revocations.Sign([pack.Digest], [], DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), revoker.PrivateKeyPem));

        FluentActions.Invoking(() => PackLoader.Load(archive, revoked)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("is revoked", StringComparison.Ordinal));
        FluentActions.Invoking(() => trust.WithRevocations(Encoding.UTF8.GetBytes("{}"))).Should().Throw<InvalidDataException>();
    }
}
