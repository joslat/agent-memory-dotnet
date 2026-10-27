using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Signed revocation documents (pack format v1.2 §8; PACK-harden, review 1.10): signed by a key the host names as revoker,
/// they add revoked digests and keys, never remove one, and bring their expiry, after which nothing verifies until a fresh
/// document is applied. A revoker key signs no packs.
/// </summary>
public sealed class RevocationTests
{
    private static readonly DateTimeOffset Issued = SamplePack.Now.AddMinutes(-10);
    private readonly SamplePack _sample = new();
    private readonly PackKey _revoker = PackKey.Create();

    private TrustPolicy Policy() => _sample.Policy() with
    {
        Keys = [.. _sample.Policy().Keys, new TrustedKey(_revoker.KeyId, _revoker.PublicKeyPem, ["io.example"], [Revocations.Role], null, null)],
    };

    private byte[] Document(IReadOnlyList<string>? digests = null, IReadOnlyList<RevokedKey>? keys = null, DateTimeOffset? expires = null, PackKey? by = null) =>
        Revocations.Sign(digests ?? [], keys ?? [], Issued, expires ?? Issued.AddDays(7), (by ?? _revoker).PrivateKeyPem);

    [Fact]
    public void A_document_from_the_revoker_refuses_the_pack_it_names_and_leaves_others()
    {
        var files = _sample.Signed();
        var digest = FilesList.DigestOf(files[PackPaths.FilesList]);

        var policy = Revocations.Apply(Policy(), Document(digests: [digest]), SamplePack.Now);

        policy.RevokedDigests.Should().Equal(digest);
        policy.RevocationsExpire.Should().Be(Issued.AddDays(7));
        _sample.Verify(files, policy).Problems.Should().Contain(p => p.Contains("is revoked", StringComparison.Ordinal));
        _sample.Verify(_sample.Signed(extraRecord: "{\"key\":\"n2\",\"title\":\"Second\",\"text\":\"Hi\"}\n"), policy).IsVerified.Should().BeTrue();
    }

    [Fact]
    public void Revocations_are_added_never_removed_and_the_earlier_expiry_holds()
    {
        var other = PackKey.Create();
        var first = Revocations.Apply(Policy(), Document(keys: [new RevokedKey(other.KeyId, null)], expires: Issued.AddDays(3)), SamplePack.Now);
        // A later document that does not name the key, or names it only as compromised at a time, cannot un-revoke it.
        var second = Revocations.Apply(first, Document(keys: [new RevokedKey(other.KeyId, Issued)], expires: Issued.AddDays(30)), SamplePack.Now);

        second.RevokedKeys.Should().ContainSingle().Which.Should().Be(new RevokedKey(other.KeyId, null));
        second.RevocationsExpire.Should().Be(Issued.AddDays(3), "the earlier expiry holds until a fresh policy is loaded");
    }

    [Fact]
    public void Of_two_compromise_times_the_earlier_holds()
    {
        var other = PackKey.Create();
        var later = Revocations.Apply(Policy(), Document(keys: [new RevokedKey(other.KeyId, Issued)]), SamplePack.Now);
        var earlier = Revocations.Apply(later, Document(keys: [new RevokedKey(other.KeyId, Issued.AddDays(-2))]), SamplePack.Now);

        earlier.RevokedKeys.Single().CompromisedAt.Should().Be(Issued.AddDays(-2));
    }

    [Fact]
    public void An_expired_document_stops_every_pack_until_a_fresh_one()
    {
        var policy = Revocations.Apply(Policy(), Document(expires: Issued.AddMinutes(5)), SamplePack.Now);

        _sample.Verify(_sample.Signed(), policy).Problems.Should().Contain(p => p.Contains("revocation list expired", StringComparison.Ordinal));
    }

    [Fact]
    public void A_document_not_signed_by_a_revoker_is_refused_and_one_from_a_revoker_that_is_not_live_only_denies()
    {
        FluentActions.Invoking(() => Revocations.Apply(Policy(), Document(by: _sample.Key), SamplePack.Now))
            .Should().Throw<InvalidDataException>().WithMessage("*revoker*");

        // Seventh review: a list from an expired or revoked revoker still denies the packs and keys it names, for the run;
        // it never decides about a revoker and brings no freshness.
        var digest = "sha256:" + new string('e', 64);
        var other = PackKey.Create();
        foreach (var policy in new[]
        {
            Policy() with { Keys = [.. Policy().Keys.Select(k => k.KeyId == _revoker.KeyId ? k with { NotAfter = Issued } : k)] },
            Policy() with { RevokedKeys = [new RevokedKey(_revoker.KeyId, null)] },
        })
        {
            var applied = Revocations.Apply(policy, Document(digests: [digest], keys: [new RevokedKey(other.KeyId, null)], expires: Issued.AddMinutes(30)), SamplePack.Now);
            applied.RevokedDigests.Should().Contain(digest);
            applied.RevokedKeys.Should().Contain(k => k.KeyId == other.KeyId);
            applied.RevocationsExpire.Should().Be(policy.RevocationsExpire, "a list without a live signer brings no freshness");
        }
    }

    [Fact]
    public void A_changed_document_or_one_issued_in_the_future_is_refused()
    {
        var envelope = JsonNode.Parse(Document())!.AsObject();
        var payload = JsonNode.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()))!.AsObject();
        payload["expires"] = "2199-01-01T00:00:00Z";
        envelope["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        FluentActions.Invoking(() => Revocations.Apply(Policy(), Encoding.UTF8.GetBytes(envelope.ToJsonString()), SamplePack.Now))
            .Should().Throw<InvalidDataException>();

        var future = Revocations.Sign([], [], SamplePack.Now.AddDays(1), SamplePack.Now.AddDays(2), _revoker.PrivateKeyPem);
        FluentActions.Invoking(() => Revocations.Apply(Policy(), future, SamplePack.Now)).Should().Throw<InvalidDataException>().WithMessage("*future*");
    }

    [Fact]
    public void A_revoker_key_signs_no_packs()
    {
        var files = _sample.Signed();
        var revokerOnly = new TrustPolicy { Keys = [new TrustedKey(_sample.Key.KeyId, _sample.Key.PublicKeyPem, ["io.example"], [Revocations.Role], null, null)] };

        _sample.Verify(files, revokerOnly).IsVerified.Should().BeFalse("its key may sign revocation documents, not packs");
        FluentActions.Invoking(() => Dsse.Sign(files[PackPaths.FilesList], _sample.Id, _sample.Version, Revocations.Role, SamplePack.SignedAt, _sample.Key.PrivateKeyPem))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_mutated_document_applies_or_is_refused_with_a_reason()
    {
        var random = new Random(1_10);
        var document = Document(digests: ["sha256:" + new string('a', 64)]);
        for (var run = 0; run < 2_000; run++)
        {
            var bytes = document.ToArray();
            for (var edits = random.Next(1, 4); edits > 0; edits--) bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            try { Revocations.Apply(Policy(), bytes, SamplePack.Now); }
            catch (InvalidDataException) { }
        }
    }
}
