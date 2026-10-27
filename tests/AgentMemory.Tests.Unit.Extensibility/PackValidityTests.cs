using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The second red team's host findings: a loaded pack stops being valid at the first of its own validUntil, its set's, the
/// revocation list's expiry and its signing key's expiry, so a long-running host switches it off then; and an older signed
/// revocation document cannot replace a newer one on a host with a ledger.
/// </summary>
public sealed class PackValidityTests : IDisposable
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly string _root = Directory.CreateTempSubdirectory("pack-validity-").FullName;
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();
    private readonly PackKey _revoker = PackKey.Create();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackTrust Trust(DateTimeOffset? keyNotAfter = null, DateTimeOffset? revocationsExpire = null)
    {
        var key = new JsonObject
        {
            ["keyId"] = PackKey.KeyIdOf(_key.PublicKeyPem), ["publicKey"] = _key.PublicKeyPem,
            ["idPrefixes"] = new JsonArray("io.agentmemory", "io.example"), ["roles"] = new JsonArray("publisher"),
        };
        if (keyNotAfter is { } until) key["notAfter"] = until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var revoker = new JsonObject
        {
            ["keyId"] = _revoker.KeyId, ["publicKey"] = _revoker.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.example"), ["roles"] = new JsonArray("revoker"),
        };
        var policy = new JsonObject { ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = new JsonArray(key, revoker) };
        if (revocationsExpire is { } expires)
            policy["revoked"] = new JsonObject { ["digests"] = new JsonArray(), ["expires"] = expires.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) };
        return PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString()));
    }

    private byte[] Archive(DateTimeOffset? validUntil = null)
    {
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        if (validUntil is { } until)
        {
            var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
            manifest["validUntil"] = until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        return PackAuthoring.Build(source, _key.PrivateKeyPem);
    }

    private static DateTimeOffset Second(DateTimeOffset at) => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());

    [Fact]
    public void A_pack_is_valid_until_the_first_of_its_own_date_its_signing_keys_and_the_revocation_list()
    {
        var soon = Second(DateTimeOffset.UtcNow.AddDays(3));
        var later = Second(DateTimeOffset.UtcNow.AddDays(30));

        PackLoader.Load(Archive(), Trust()).ValidUntil.Should().BeNull("nothing expires");
        PackLoader.Load(Archive(validUntil: later), Trust(keyNotAfter: soon)).ValidUntil.Should().Be(soon, "the signing key expires first");
        PackLoader.Load(Archive(validUntil: later), Trust(revocationsExpire: soon)).ValidUntil.Should().Be(soon, "the revocation list goes stale first");
        PackLoader.Load(Archive(validUntil: soon), Trust(keyNotAfter: later)).ValidUntil.Should().Be(soon);
    }

    [Fact]
    public void A_sets_validity_caps_every_pack_in_it()
    {
        var soon = Second(DateTimeOffset.UtcNow.AddDays(2));
        var set = PackAuthoring.BuildSet("io.example.dojo-set", "1.0.0", [Archive()], _key.PrivateKeyPem);

        // The set's own validity: its revocation list here.
        var loaded = PackFamilies.LoadSet(set, Trust(revocationsExpire: soon));

        loaded.ValidUntil.Should().Be(soon);
        loaded.Packs.Should().ContainSingle().Which.ValidUntil.Should().Be(soon);
    }

    [Fact]
    public void An_older_revocation_document_than_the_newest_applied_is_refused_on_a_host_with_a_ledger()
    {
        var ledger = PackInstallLedger.Open(Path.Combine(_root, "ledger.json"));
        var now = DateTimeOffset.UtcNow;
        var newer = Revocations.Sign(["sha256:" + new string('a', 64)], [], now.AddMinutes(-1), now.AddDays(7), _revoker.PrivateKeyPem);
        var older = Revocations.Sign([], [], now.AddDays(-1), now.AddDays(7), _revoker.PrivateKeyPem);

        Trust().WithRevocations(newer, ledger);
        Trust().WithRevocations(newer, PackInstallLedger.Open(Path.Combine(_root, "ledger.json"))).Should().NotBeNull("the same document again is fine");

        FluentActions.Invoking(() => Trust().WithRevocations(older, PackInstallLedger.Open(Path.Combine(_root, "ledger.json"))))
            .Should().Throw<InvalidDataException>().WithMessage("*before the one this host applied*");
        Trust().WithRevocations(older).Should().NotBeNull("without a ledger there is no history to hold it to");
    }
}
