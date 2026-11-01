#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Features 16 and 17: serve's resources and read-only memory folder, through one door.</summary>
public sealed class PackResourcesTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> None = [];

    private static PackMount Mount()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["live.md"] = Encoding.UTF8.GetBytes("Still true."),
            ["over.md"] = Encoding.UTF8.GetBytes("---\nvalidTo: 2026-10-01\n---\nNo longer true."),
        };
        var archive = PackImports.FromFolder(files, null, "com.example.folder", "1.0.0", "Example Owner", Key.PrivateKeyPem).Archive;
        return PackMemory.Mount([archive], PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example"))));
    }

    [Fact]
    public void A_served_pack_answers_its_live_records_as_files_and_resources()
    {
        var mount = Mount();
        var digest = mount.Packs.Single().Digest;
        var folder = JsonNode.Parse(PackResources.MemoryView(mount, "/memories/com.example.folder", _ => null, None, Now))!;
        var entry = folder["entries"]!.AsArray().Single()!.GetValue<string>();
        entry.Should().EndWith("/live.md", "a record past its life is not a file");
        var type = entry[..entry.IndexOf('/')];
        JsonNode.Parse(PackResources.MemoryView(mount, "/memories/com.example.folder/" + entry, _ => null, None, Now))!["text"]!.GetValue<string>().Should().Contain("Still true.");
        JsonNode.Parse(PackResources.Record(mount, digest, type, "live.md", _ => null, Now))!["key"]!.GetValue<string>().Should().Be("live.md");
        JsonNode.Parse(PackResources.Record(mount, digest, type, "over.md", _ => null, Now))!["error"].Should().NotBeNull();
        JsonNode.Parse(PackResources.Card(mount, digest["sha256:".Length..], _ => null))!["card"]!["owner"]!.GetValue<string>().Should().Be("Example Owner");
        JsonNode.Parse(PackResources.MemoryView(mount, "/etc/passwd", _ => null, None, Now))!["error"].Should().NotBeNull();
    }

    [Fact]
    public void A_pack_that_is_off_answers_with_its_reason_and_never_with_records()
    {
        var mount = Mount();
        var digest = mount.Packs.Single().Digest;
        static string? Off(LoadedPack p) => "its usage policy does not allow rehydrate here";
        foreach (var answer in new[]
        {
            PackResources.MemoryView(mount, "/memories/com.example.folder", Off, None, Now),
            PackResources.MemoryView(mount, "/memories/com.example.folder/FolderNote/live.md", Off, None, Now),
            PackResources.Record(mount, digest, "FolderNote", "live.md", Off, Now),
            PackResources.Card(mount, digest, Off),
        })
            answer.Should().Contain("usage policy").And.NotContain("Still true");
        JsonNode.Parse(PackResources.Status(mount, "com.example.folder", Off, None))!.AsArray().Single()!["served"]!.GetValue<bool>().Should().BeFalse();
        var gated = new Dictionary<string, string> { ["com.example.other"] = "kept off by the host's trial gate" };
        JsonNode.Parse(PackResources.Status(mount, "com.example.other", _ => null, gated))!["reason"]!.GetValue<string>().Should().Contain("trial gate");
        JsonNode.Parse(PackResources.MemoryView(mount, "/memories/com.example.other", _ => null, gated, Now))!["error"]!.GetValue<string>().Should().Contain("trial gate");
        JsonNode.Parse(PackResources.MemoryView(mount, "/memories", _ => null, gated, Now))!["entries"]!.AsArray()
            .Should().Contain(e => e!["name"]!.GetValue<string>() == "com.example.other/" && !e["served"]!.GetValue<bool>());
    }
}
