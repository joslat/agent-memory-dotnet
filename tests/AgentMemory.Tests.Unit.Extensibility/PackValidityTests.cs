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
            .Should().Throw<InvalidDataException>().WithMessage("*no later than the one this host applied*");
        Trust().WithRevocations(older).Should().NotBeNull("without a ledger there is no history to hold it to");
    }

    [Fact]
    public void A_host_that_applied_a_revocation_list_does_not_verify_without_it()
    {
        // Third review: leaving the list out rolled the host back as surely as an older list.
        var path = Path.Combine(_root, "ledger.json");
        var now = DateTimeOffset.UtcNow;
        var list = Revocations.Sign([], [], now.AddMinutes(-1), now.AddDays(7), _revoker.PrivateKeyPem);
        Trust().WithRevocations(list, PackInstallLedger.Open(path));

        FluentActions.Invoking(() => PackLoader.Load(Archive(), Trust(), PackInstallLedger.Open(path)))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("give that list or a newer one"));
        PackLoader.Load(Archive(), Trust().WithRevocations(list), PackInstallLedger.Open(path)).Should().NotBeNull();
        PackLoader.Load(Archive(), Trust()).Should().NotBeNull("a host without a ledger keeps no history");
    }

    [Fact]
    public void Each_revoker_keeps_its_own_mark()
    {
        var second = PackKey.Create();
        var path = Path.Combine(_root, "ledger.json");
        var now = DateTimeOffset.UtcNow;
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(TrustJsonWithRevokers(second)));
        var fromFirst = Revocations.Sign([], [], now.AddMinutes(-1), now.AddDays(7), _revoker.PrivateKeyPem);
        var fromSecondEarlier = Revocations.Sign([], [], now.AddMinutes(-2), now.AddDays(7), second.PrivateKeyPem);

        trust.WithRevocations(fromFirst, PackInstallLedger.Open(path));
        // Issued before the first revoker's list, by the other revoker: its own history, so it applies.
        trust.WithRevocations(fromSecondEarlier, PackInstallLedger.Open(path)).Should().NotBeNull();
    }

    [Fact]
    public void Another_list_in_the_same_second_counts_as_older()
    {
        // Fourth review: lists are written to the second, so a full-precision mark alone let a second list of that second through.
        var path = Path.Combine(_root, "ledger.json");
        var second = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
        var revoking = Revocations.Sign(["sha256:" + new string('c', 64)], [], second, second.AddDays(7), _revoker.PrivateKeyPem);
        var forgetting = Revocations.Sign([], [], second, second.AddDays(7), _revoker.PrivateKeyPem);
        Trust().WithRevocations(revoking, PackInstallLedger.Open(path));

        Trust().WithRevocations(revoking, PackInstallLedger.Open(path)).Should().NotBeNull("the same list again");
        FluentActions.Invoking(() => Trust().WithRevocations(forgetting, PackInstallLedger.Open(path))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Revocation_marks_never_lock_a_host_out()
    {
        var second = PackKey.Create();
        var now = DateTimeOffset.UtcNow;
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(TrustJsonWithRevokers(second)));
        var current = Revocations.Sign([], [], now.AddMinutes(-1), now.AddDays(7), _revoker.PrivateKeyPem);

        // (a) An old, expired list from the other revoker, replayed: it leaves no mark.
        var path = Path.Combine(_root, "a.json");
        trust.WithRevocations(current, PackInstallLedger.Open(path));
        var expired = Revocations.Sign([], [], now.AddDays(-30), now.AddDays(-20), second.PrivateKeyPem);
        trust.WithRevocations(expired, PackInstallLedger.Open(path));
        PackInstallLedger.Open(path).Withheld(trust.WithRevocations(current)).Should().BeNull();

        // (b) A revoker revoked since asks for nothing: the other revoker revokes the first.
        path = Path.Combine(_root, "b.json");
        trust.WithRevocations(current, PackInstallLedger.Open(path));
        var revokesFirst = Revocations.Sign([], [new RevokedKey(_revoker.KeyId, null)], now.AddMinutes(-1), now.AddDays(7), second.PrivateKeyPem);
        PackInstallLedger.Open(path).Withheld(trust.WithRevocations(revokesFirst)).Should().BeNull();

        // (c) One list signed by both, its signatures reordered: both marks are kept, and the same list meets both.
        path = Path.Combine(_root, "c.json");
        var both = CoSigned(current, second);
        trust.WithRevocations(both, PackInstallLedger.Open(path));
        PackInstallLedger.Open(path).Withheld(trust.WithRevocations(Reversed(both))).Should().BeNull();
        PackInstallLedger.Open(path).Withheld(trust).Should().NotBeNull("without the list a host that applied it is held back");
    }

    // A revocation document signed again by another revoker: the same payload, a second signature entry.
    private static byte[] CoSigned(byte[] document, PackKey other)
    {
        var envelope = JsonNode.Parse(document)!.AsObject();
        var payload = Convert.FromBase64String(envelope["payload"]!.GetValue<string>());
        var type = envelope["payloadType"]!.GetValue<string>();
        var pae = Encoding.UTF8.GetBytes($"DSSEv1 {Encoding.UTF8.GetByteCount(type)} {type} {payload.Length} ").Concat(payload).ToArray();
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(other.PrivateKeyPem);
        var sig = key.SignData(pae, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
        envelope["signatures"]!.AsArray().Add(new JsonObject { ["keyid"] = other.KeyId, ["sig"] = Convert.ToBase64String(sig) });
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    private static byte[] Reversed(byte[] document)
    {
        var envelope = JsonNode.Parse(document)!.AsObject();
        var signatures = envelope["signatures"]!.AsArray().Select(s => s!.DeepClone()).Reverse().ToArray();
        envelope["signatures"] = new JsonArray(signatures);
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    private string TrustJsonWithRevokers(PackKey second)
    {
        var keys = new JsonArray(
            new JsonObject { ["keyId"] = PackKey.KeyIdOf(_key.PublicKeyPem), ["publicKey"] = _key.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.agentmemory"), ["roles"] = new JsonArray("publisher") },
            new JsonObject { ["keyId"] = _revoker.KeyId, ["publicKey"] = _revoker.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.example"), ["roles"] = new JsonArray("revoker") },
            new JsonObject { ["keyId"] = second.KeyId, ["publicKey"] = second.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.example"), ["roles"] = new JsonArray("revoker") });
        return new JsonObject { ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = keys }.ToJsonString();
    }
}
