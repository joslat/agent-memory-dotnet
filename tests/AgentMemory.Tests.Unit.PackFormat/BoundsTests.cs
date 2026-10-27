using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The fifth review's bounds before any signature holds. Anyone may add an envelope to a genuine pack or a signature entry to
/// an envelope, and a signature check used to hash the whole payload again for every entry: an 8 MiB envelope in a 65 KB
/// archive took 90 s. Envelopes, entries, versions and revocation documents are now bounded, and each envelope is hashed once.
/// </summary>
public sealed class BoundsTests
{
    private readonly SamplePack _sample = new();

    private Dictionary<string, byte[]> Signed() => _sample.Signed();

    private static (string Path, JsonObject Envelope) EnvelopeOf(Dictionary<string, byte[]> files)
    {
        var path = files.Keys.Single(PackPaths.IsSignature);
        return (path, JsonNode.Parse(files[path])!.AsObject());
    }

    [Fact]
    public void An_envelope_larger_than_64_KiB_refuses_the_pack_before_it_is_read()
    {
        var files = Signed();
        var (_, envelope) = EnvelopeOf(files);
        var statement = JsonNode.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()))!.AsObject();
        statement["padding"] = new string('x', 8 << 20);
        var forged = new JsonObject
        {
            ["payloadType"] = Dsse.PayloadType, ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(statement.ToJsonString())),
            ["signatures"] = new JsonArray([.. Enumerable.Range(0, 1000).Select(_ => (JsonNode)new JsonObject { ["keyid"] = _sample.Key.KeyId, ["sig"] = "AAAA" })]),
        };
        files[PackPaths.SignaturesFolder + "zz.dsse.json"] = Encoding.UTF8.GetBytes(forged.ToJsonString());

        var clock = Stopwatch.StartNew();
        var verification = _sample.Verify(files);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "it took 90 s before");
        verification.Pack.Should().BeNull();
        verification.Problems.Should().Contain(p => p.Contains("larger than 64 KiB"));
    }

    [Fact]
    public void An_envelope_holds_at_most_16_signature_entries()
    {
        foreach (var (extra, verifies) in new[] { (15, true), (16, false) })
        {
            var files = Signed();
            var (path, envelope) = EnvelopeOf(files);
            for (var i = 0; i < extra; i++) envelope["signatures"]!.AsArray().Add(new JsonObject { ["keyid"] = new string('0', 64), ["sig"] = "AAAA" });
            files[path] = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            (_sample.Verify(files).Pack is not null).Should().Be(verifies, $"{extra + 1} entries");
        }
    }

    [Fact]
    public void A_pack_carries_at_most_16_envelopes()
    {
        foreach (var (copies, verifies) in new[] { (15, true), (16, false) })
        {
            var files = Signed();
            var (path, _) = EnvelopeOf(files);
            for (var i = 0; i < copies; i++) files[$"{PackPaths.SignaturesFolder}copy-{i:00}.dsse.json"] = files[path];
            (_sample.Verify(files).Pack is not null).Should().Be(verifies, $"{copies + 1} envelopes");
        }
    }

    [Fact]
    public void A_version_is_at_most_256_characters()
    {
        SemVer.TryParse("1.0.0-" + new string('a', 250), out _).Should().BeTrue();
        SemVer.TryParse("1.0.0-" + new string('a', 251), out _).Should().BeFalse();
        SemVer.TryParse("1.0.0-" + string.Join('.', Enumerable.Repeat("a", 1 << 20)), out _).Should().BeFalse("a million identifiers are never split");
    }

    [Fact]
    public void A_revocation_document_is_bounded_in_size_and_signatures()
    {
        var revoker = PackKey.Create();
        var policy = _sample.Policy() with
        {
            Keys = [.. _sample.Policy().Keys, new TrustedKey(revoker.KeyId, revoker.PublicKeyPem, ["io.example"], [Revocations.Role], null, null)],
        };
        var document = JsonNode.Parse(Revocations.Sign([], [], SamplePack.Now.AddMinutes(-1), SamplePack.Now.AddDays(7), revoker.PrivateKeyPem))!.AsObject();
        var signatures = document["signatures"]!.AsArray();
        for (var i = 0; i < 16; i++) signatures.Insert(0, new JsonObject { ["keyid"] = revoker.KeyId, ["sig"] = "AAAA" });
        FluentActions.Invoking(() => Revocations.Apply(policy, Encoding.UTF8.GetBytes(document.ToJsonString()), SamplePack.Now))
            .Should().Throw<InvalidDataException>().WithMessage("*more than 16 signatures*");

        signatures.RemoveAt(0);
        Revocations.Apply(policy, Encoding.UTF8.GetBytes(document.ToJsonString()), SamplePack.Now).Should().NotBeNull("16 entries, the last one genuine");

        var large = new byte[Revocations.MaximumDocumentBytes + 1];
        FluentActions.Invoking(() => Revocations.Apply(policy, large, SamplePack.Now)).Should().Throw<InvalidDataException>().WithMessage("*larger than 4 MiB*");
    }
}
