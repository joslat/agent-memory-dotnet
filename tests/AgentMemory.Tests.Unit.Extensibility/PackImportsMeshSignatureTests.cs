#pragma warning disable AMEXT001
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 15, R2: Mesh Memory Protocol blocks verified as the mesh verifies them, against sym's own signed records
/// (Fixtures/Mesh/sym-signed-records.json, from sym's test fixtures, Apache License 2.0).
/// </summary>
public sealed class PackImportsMeshSignatureTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();
    private static readonly JsonObject Vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mesh", "sym-signed-records.json")))!.AsObject();

    private static bool Ed25519Verifies(byte[] publicKey, byte[] signature, byte[] message) =>
        Ed25519.ValidatePublicKeyFull(publicKey, 0) && Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);

    private static byte[] FromBase64Url(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));

    private static ImportResult Import(JsonObject record, bool keyed = true)
    {
        var author = record["metadata"]!["createdByNodeId"]!.GetValue<string>();
        var keys = keyed ? new Dictionary<string, byte[]> { [author] = FromBase64Url(Vectors["publicKeyBase64url"]!.GetValue<string>()) } : null;
        return PackImports.FromMeshMemory(new Dictionary<string, byte[]> { ["block.json"] = Encoding.UTF8.GetBytes(record.ToJsonString()) },
            "com.example.mesh", "1.0.0", "Example Owner", Key.PrivateKeyPem, keys, Ed25519Verifies);
    }

    private static string Derivation(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive));
        using var reader = new StreamReader(zip.GetEntry("META/derivation.json")!.Open());
        return reader.ReadToEnd();
    }

    public static TheoryData<string> Accepted() => [.. Vectors["cases"]!.AsArray().Where(c => c!["accepted"]!.GetValue<bool>()).Select(c => c!["label"]!.GetValue<string>())];

    private static JsonObject Record(string label) =>
        Vectors["cases"]!.AsArray().Single(c => c!["label"]!.GetValue<string>() == label)!["record"]!.DeepClone().AsObject();

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Each_signed_record_sym_accepts_verifies_under_its_authors_key(string label)
    {
        Derivation(Import(Record(label)).Archive).Should().Contain("1 block(s) verified");
        Derivation(Import(Record(label), keyed: false).Archive).Should().Contain("0 block(s) verified").And.Contain("1 signed by an author no key was given for");
    }

    [Fact]
    public void A_record_sym_refuses_or_one_whose_text_or_signed_fields_changed_refuses_the_store()
    {
        FluentActions.Invoking(() => Import(Record("a rewritten meta.key"))).Should().Throw<InvalidDataException>().WithMessage("*category's key*");
        var label = Vectors["cases"]!.AsArray().First(c => c!["accepted"]!.GetValue<bool>())!["label"]!.GetValue<string>();

        var text = Record(label);
        text["categories"]!["focus"]!["text"] = "something else";
        FluentActions.Invoking(() => Import(text)).Should().Throw<InvalidDataException>().WithMessage("*address*");

        var room = Record(label);
        room["metadata"]!["room"] = "another-room";
        FluentActions.Invoking(() => Import(room)).Should().Throw<InvalidDataException>().WithMessage("*does not verify*");
    }
}
