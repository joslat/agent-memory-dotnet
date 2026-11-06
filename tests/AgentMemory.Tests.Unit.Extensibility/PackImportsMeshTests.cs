#pragma warning disable AMEXT001
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Feature 15, R1 and R2: a Mesh Memory Protocol store's blocks become notes; a signature it cannot read refuses it.</summary>
public sealed class PackImportsMeshTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();

    // An entry as a sym 0.14 store writes one: the signed two-section block, and the store's own notes beside it.
    private static byte[] Entry(string key, string lifecycle = "observed", bool signed = false) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["key"] = key, ["content"] = "flat text", ["lifecycle"] = lifecycle, ["storedAt"] = 1767225600000,
        ["cmb"] = new JsonObject
        {
            ["categories"] = new JsonObject
            {
                ["focus"] = new JsonObject { ["text"] = "the release", ["meta"] = null }, ["issue"] = new JsonObject { ["text"] = "", ["meta"] = null },
                ["intent"] = new JsonObject { ["text"] = "ship on Friday", ["meta"] = null }, ["recommendation"] = "escalate",
            },
            ["metadata"] = signed
                ? new JsonObject { ["key"] = key, ["createdBy"] = "agent-a", ["createdTimestamp"] = 1767225600000, ["sig"] = "c2lnbmF0dXJl" }
                : new JsonObject { ["key"] = key, ["createdTimestamp"] = 1767225600000 },
        },
        ["lineage"] = new JsonObject { ["parents"] = new JsonArray("cmb-0") },
    }.ToJsonString());

    private static ImportResult Import(Dictionary<string, byte[]> files) =>
        PackImports.FromMeshMemory(files, "com.example.mesh", "1.0.0", "Example Owner", Key.PrivateKeyPem);

    private static string Derivation(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive));
        using var reader = new StreamReader(zip.GetEntry("META/derivation.json")!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void Each_block_becomes_a_note_of_its_categories_in_order_and_a_superseded_block_is_left_out()
    {
        var result = Import(new(StringComparer.Ordinal)
        {
            ["cmb-1.json"] = Entry("cmb-1"), ["cmb-2.json"] = Entry("cmb-2", "superseded"), ["migration-v1.json"] = "{}"u8.ToArray(),
            ["sub/cmb-3.json"] = Entry("cmb-3"),
        });
        result.Records.Should().Be(1);
        result.Skipped.Should().ContainSingle(s => s.Contains("cmb-2") && s.Contains("superseded"));
        result.Dropped.Keys.Should().Contain(["category 'recommendation'", "entry.lineage", "file 'sub/cmb-3.json'"]);
    }

    [Fact]
    public void A_signature_that_is_not_ed25519_refuses_the_store_and_an_unsigned_store_records_absent()
    {
        FluentActions.Invoking(() => Import(new(StringComparer.Ordinal) { ["cmb-1.json"] = Entry("cmb-1", signed: true) }))
            .Should().Throw<InvalidDataException>().WithMessage("*not ed25519*");
        Derivation(Import(new(StringComparer.Ordinal) { ["cmb-1.json"] = Entry("cmb-1") }).Archive).Should().Contain("\"sourceSignature\":\"absent\"");
    }
}
