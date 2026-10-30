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
/// revocation list's expiry and its signing key's expiry, so a long-running host switches it off then; and a host with a ledger
/// keeps what it revoked, whatever list a later run is given.
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
        var set = PackAuthoring.BuildSet("io.example.dojo-set", "1.0.0", [Archive()], _key.PrivateKeyPem, Trust());

        // The set's own validity: its revocation list here.
        var loaded = PackFamilies.LoadSet(set, Trust(revocationsExpire: soon));

        loaded.ValidUntil.Should().Be(soon);
        loaded.Packs.Should().ContainSingle().Which.ValidUntil.Should().Be(soon);
    }

    [Fact]
    public void What_a_host_revoked_stays_revoked_whatever_list_a_later_run_is_given()
    {
        // Red team 8 and the third review: an older list, or none, must not roll the host back. Fifth review: the host keeps
        // what it revoked, so nothing has to be given again and nothing can lock it out.
        var path = Path.Combine(_root, "ledger.json");
        var archive = Archive();
        var digest = PackLoader.Load(archive, Trust()).Digest;
        var now = DateTimeOffset.UtcNow;
        var revoking = Revocations.Sign([digest], [], now.AddMinutes(-1), now.AddDays(7), _revoker.PrivateKeyPem);
        var older = Revocations.Sign([], [], now.AddDays(-1), now.AddDays(7), _revoker.PrivateKeyPem);
        Trust().WithRevocations(revoking, PackInstallLedger.Open(path));

        Revoked(() => PackLoader.Load(archive, Trust().WithRevocations(older, PackInstallLedger.Open(path)), PackInstallLedger.Open(path)));
        Revoked(() => PackLoader.Load(archive, Trust(), PackInstallLedger.Open(path)));
        Revoked(() => PackLoader.Load(archive, PackInstallLedger.Open(path).WithKeptRevocations(Trust())));
        PackLoader.Load(archive, Trust()).Should().NotBeNull("a host without a ledger keeps no history");
    }

    [Fact]
    public void A_revocation_outlives_its_revoker()
    {
        // Fifth review: once the revoker's key was itself revoked, or expired, everything it revoked lapsed.
        var second = PackKey.Create();
        var path = Path.Combine(_root, "ledger.json");
        var archive = Archive();
        var now = DateTimeOffset.UtcNow;
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(TrustJsonWithRevokers(second)));
        var revoking = Revocations.Sign([PackLoader.Load(archive, trust).Digest], [], now.AddMinutes(-2), now.AddDays(7), _revoker.PrivateKeyPem);
        trust.WithRevocations(revoking, PackInstallLedger.Open(path));
        var revokesFirst = Revocations.Sign([], [new RevokedKey(_revoker.KeyId, null)], now.AddMinutes(-1), now.AddDays(7), second.PrivateKeyPem);
        var afterwards = trust.WithRevocations(revokesFirst, PackInstallLedger.Open(path));

        // Given again now, the revoked revoker's list still denies for this run; it adds nothing to the ledger.
        Revoked(() => PackLoader.Load(archive, afterwards.WithRevocations(revoking)));
        Revoked(() => PackLoader.Load(archive, afterwards, PackInstallLedger.Open(path)));
        Revoked(() => PackLoader.Load(archive, trust, PackInstallLedger.Open(path)));
    }

    [Fact]
    public void Co_signed_and_older_lists_never_lock_a_host_out()
    {
        // Fifth review: a mark per co-signer refused the co-signed list once one signer issued a newer list alone, and the
        // newer list alone was held back for the other signer's mark.
        var second = PackKey.Create();
        var path = Path.Combine(_root, "ledger.json");
        var now = DateTimeOffset.UtcNow;
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(TrustJsonWithRevokers(second)));
        var both = CoSigned(Revocations.Sign([], [], now.AddMinutes(-20), now.AddDays(7), _revoker.PrivateKeyPem), second);
        var firstAlone = Revocations.Sign([], [], now.AddMinutes(-1), now.AddDays(7), _revoker.PrivateKeyPem);
        trust.WithRevocations(both, PackInstallLedger.Open(path));
        trust.WithRevocations(both, PackInstallLedger.Open(path)).WithRevocations(firstAlone, PackInstallLedger.Open(path));

        var archive = Archive();
        PackLoader.Load(archive, trust.WithRevocations(both, PackInstallLedger.Open(path)), PackInstallLedger.Open(path)).Should().NotBeNull();
        PackLoader.Load(archive, trust.WithRevocations(firstAlone, PackInstallLedger.Open(path)), PackInstallLedger.Open(path)).Should().NotBeNull();
        PackLoader.Load(archive, trust.WithRevocations(Reversed(both)), PackInstallLedger.Open(path)).Should().NotBeNull();
        PackLoader.Load(archive, trust, PackInstallLedger.Open(path)).Should().NotBeNull();
    }

    [Fact]
    public void A_list_from_the_same_second_or_an_expired_one_removes_nothing()
    {
        var path = Path.Combine(_root, "ledger.json");
        var archive = Archive();
        var digest = PackLoader.Load(archive, Trust()).Digest;
        var second = Second(DateTimeOffset.UtcNow.AddMinutes(-1));
        Trust().WithRevocations(Revocations.Sign([digest], [], second, second.AddDays(7), _revoker.PrivateKeyPem), PackInstallLedger.Open(path));

        var forgetting = Revocations.Sign([], [], second, second.AddDays(7), _revoker.PrivateKeyPem);
        Revoked(() => PackLoader.Load(archive, Trust().WithRevocations(forgetting, PackInstallLedger.Open(path)), PackInstallLedger.Open(path)));
        var expired = Revocations.Sign([], [], second.AddDays(-30), second.AddDays(-20), _revoker.PrivateKeyPem);
        Trust().WithRevocations(expired, PackInstallLedger.Open(path));
        Revoked(() => PackLoader.Load(archive, Trust(), PackInstallLedger.Open(path)));
    }

    [Fact]
    public void Allowing_a_set_downgrade_still_holds_one_content_per_version()
    {
        // Sixth review: a set installed with --allow-downgrade skipped the ledger, so a member version could change content.
        var store = PackStore.Open(Path.Combine(_root, "store"));
        store.Install(PackAuthoring.BuildSet("io.example.dojo-set", "1.0.0", [Archive()], _key.PrivateKeyPem, Trust()), Trust());
        var changed = PackAuthoring.BuildSet("io.example.dojo-set", "0.9.0", [Archive(validUntil: Second(DateTimeOffset.UtcNow.AddDays(90)))], _key.PrivateKeyPem, Trust());

        FluentActions.Invoking(() => store.Install(changed, Trust(), allowDowngrade: true))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("other content"));
    }

    [Fact]
    public void Allowing_a_set_downgrade_installs_a_member_older_than_the_host_had()
    {
        // Seventh review: the set path passed the flag to the set but not to its members.
        var store = PackStore.Open(Path.Combine(_root, "store"));
        store.Install(Versioned("2.0.0"), Trust());
        var set = PackAuthoring.BuildSet("io.example.dojo-set", "1.0.0", [Versioned("1.0.0")], _key.PrivateKeyPem, Trust());
        // A pack is installed alone or in a set, never both (review 1, M8); the ledger still remembers 2.0.0 once it is removed.
        FluentActions.Invoking(() => store.Install(set, Trust(), allowDowngrade: true)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("already installed here"));
        store.Uninstall("io.agentmemory.packs.martialarts").Should().BeTrue();

        FluentActions.Invoking(() => store.Install(set, Trust())).Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("downgrade"));
        store.Install(set, Trust(), allowDowngrade: true).Should().NotBeNull();
    }

    // The martial arts pack at another version.
    private byte[] Versioned(string version)
    {
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["version"] = version;
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        return PackAuthoring.Build(source, _key.PrivateKeyPem);
    }

    private static void Revoked(Func<object> load) =>
        FluentActions.Invoking(load).Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("is revoked"));

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
