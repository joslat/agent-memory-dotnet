#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Feature 17, R3: listings say what the pack says, and nothing it does not.</summary>
public sealed class PackListingsTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();

    private static LoadedPack Pack()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["a.md"] = Encoding.UTF8.GetBytes("A note.") };
        var archive = PackImports.FromFolder(files, null, "com.example.listed", "1.2.3", "Example Owner", Key.PrivateKeyPem).Archive;
        return PackLoader.Load(archive, PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example"))));
    }

    [Fact]
    public void Each_listing_round_trips_the_card_and_the_pack_and_claims_no_target_a_certificate_does_not_carry()
    {
        var pack = Pack();
        var a2a = JsonNode.Parse(PackListings.A2a(pack, "https://agents.example/memory").Json)!;
        a2a["version"]!.GetValue<string>().Should().Be("1.2.3");
        a2a["description"]!.GetValue<string>().Should().Be(pack.Card!.IntendedUse);
        a2a["supportedInterfaces"]![0]!["url"]!.GetValue<string>().Should().Be("https://agents.example/memory");
        var ext = a2a["capabilities"]!["extensions"]![0]!;
        ext["uri"]!.GetValue<string>().Should().Be(PackListings.Extension);
        ext["params"]!["digest"]!.GetValue<string>().Should().Be(pack.Digest);
        ext["params"]!["limitations"]!.GetValue<string>().Should().Be(pack.Card.Limitations);

        var mcp = PackListings.McpRegistry(pack, "io.github.example/listed");
        var server = JsonNode.Parse(mcp.Json)!;
        server["$schema"]!.GetValue<string>().Should().Be(PackListings.McpSchema);
        server["description"]!.GetValue<string>().Length.Should().BeLessThanOrEqualTo(100);
        server["_meta"]!["io.modelcontextprotocol.registry/publisher-provided"]![PackListings.Extension]!["id"]!.GetValue<string>().Should().Be("com.example.listed");
        FluentActions.Invoking(() => PackListings.McpRegistry(pack, "no-slash")).Should().Throw<ArgumentException>();

        var oasf = JsonNode.Parse(PackListings.Oasf(pack, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), null).Json)!;
        oasf["authors"]![0]!.GetValue<string>().Should().Be("Example Owner");
        oasf["created_at"]!.GetValue<string>().Should().Be("2026-11-01T00:00:00Z");
        oasf["annotations"]![PackListings.Extension + ":digest"]!.GetValue<string>().Should().Be(pack.Digest);

        foreach (var json in new[] { a2a.ToJsonString(), mcp.Json, oasf.ToJsonString() })
            json.Should().NotContainAny(["noHarm", "safety"], "a listing cites only what certificates state, and they never state these");
    }
}
