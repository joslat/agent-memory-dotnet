using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The sixth review: a host with a ledger stays as fresh as the newest list it applied, whatever lists a later run is given;
/// a revoker revoked in one run signs nothing in the next; lists that revoke each other's signers are refused; a signature
/// is strict DER in both verifiers; and the ledger survives two runs saving at once.
/// </summary>
public sealed class RevocationRunTests : IDisposable
{
    private static readonly DateTimeOffset Now = SamplePack.Now;
    private readonly SamplePack _sample = new();
    private readonly PackKey _first = PackKey.Create();
    private readonly PackKey _second = PackKey.Create();
    private readonly string _root = Directory.CreateTempSubdirectory("revocation-runs-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string LedgerPath => Path.Combine(_root, "ledger.json");

    private TrustPolicy Policy() => _sample.Policy() with
    {
        Keys = [.. _sample.Policy().Keys,
            new TrustedKey(_first.KeyId, _first.PublicKeyPem, ["io.example"], [Revocations.Role], null, null),
            new TrustedKey(_second.KeyId, _second.PublicKeyPem, ["io.example"], [Revocations.Role], null, null)],
    };

    private static byte[] List(PackKey by, DateTimeOffset issued, DateTimeOffset expires, IReadOnlyList<string>? digests = null, IReadOnlyList<RevokedKey>? keys = null) =>
        Revocations.Sign(digests ?? [], keys ?? [], issued, expires, by.PrivateKeyPem);

    private PackVerification VerifyAt(DateTimeOffset at, TrustPolicy policy, HostLedger? ledger) =>
        PackVerifier.Verify(new PackContent(_sample.Signed(), "test"), policy, at, ledger);

    private static bool Stale(PackVerification verification) => verification.Problems.Any(p => p.Contains("revocation list expired", StringComparison.Ordinal));

    [Fact]
    public void A_host_with_a_ledger_stays_only_as_fresh_as_the_newest_list_it_applied()
    {
        var ledger = HostLedger.Load(LedgerPath);
        var newest = List(_first, Now.AddMinutes(-10), Now.AddHours(1));
        Revocations.ApplyAll(Policy(), [newest], Now, ledger, out _);
        ledger.Save(LedgerPath);
        var later = Now.AddHours(2);

        Stale(VerifyAt(later, Revocations.ApplyAll(Policy(), [], later, HostLedger.Load(LedgerPath), out _), HostLedger.Load(LedgerPath)))
            .Should().BeTrue("leaving the lists out no longer keeps the host fresh");
        var older = List(_first, Now.AddDays(-1), Now.AddDays(30));
        Stale(VerifyAt(later, Revocations.ApplyAll(Policy(), [older], later, HostLedger.Load(LedgerPath), out _), HostLedger.Load(LedgerPath)))
            .Should().BeTrue("an older list adds its revocations, never its expiry");
        var renewed = List(_second, Now.AddHours(1), Now.AddDays(1));
        Stale(VerifyAt(later, Revocations.ApplyAll(Policy(), [renewed], later, HostLedger.Load(LedgerPath), out _), HostLedger.Load(LedgerPath)))
            .Should().BeFalse("a newer list from any revoker renews the host");

        var retired = HostLedger.Load(LedgerPath);
        var stillHolding = HostLedger.Load(LedgerPath);
        retired.RetireRevocationLists();
        retired.Save(LedgerPath);
        Stale(VerifyAt(Now.AddDays(5), Policy(), HostLedger.Load(LedgerPath))).Should().BeFalse("a host that stops using lists forgets their freshness, saved");
        stillHolding.Save(LedgerPath);
        Stale(VerifyAt(Now.AddDays(5), Policy(), HostLedger.Load(LedgerPath))).Should().BeFalse("a run that still held the old mark does not bring it back");
        var replayed = HostLedger.Load(LedgerPath);
        Revocations.ApplyAll(Policy(), [newest], Now, replayed, out _);
        replayed.Freshness.Should().BeNull("a list issued before the retirement does not re-establish freshness");
    }

    [Fact]
    public void A_revoker_revoked_in_one_run_signs_nothing_in_the_next()
    {
        var digest = _sample.Verify(_sample.Signed()).Pack!.Digest;
        var ledger = HostLedger.Load(LedgerPath);
        Revocations.ApplyAll(Policy(), [List(_second, Now.AddMinutes(-20), Now.AddDays(7), keys: [new RevokedKey(_first.KeyId, null)])], Now, ledger, out _);
        ledger.Save(LedgerPath);

        var next = HostLedger.Load(LedgerPath);
        var fromRevoked = List(_first, Now.AddMinutes(-5), Now.AddDays(7), digests: [digest]);
        var policy = Revocations.ApplyAll(Policy(), [fromRevoked], Now, next, out var notApplied);

        notApplied.Should().ContainSingle().Which.Should().Contain("this run only");
        policy.RevokedDigests.Should().Contain(digest, "within the run it still denies: fail closed");
        next.RevokedDigests.Should().NotContain(digest, "nothing a revoked revoker says is kept");
    }

    [Fact]
    public void Within_one_run_a_list_whose_signer_another_revokes_only_denies_whatever_the_order()
    {
        var digest = "sha256:" + new string('d', 64);
        var fromFirst = List(_first, Now.AddMinutes(-5), Now.AddDays(7), digests: [digest]);
        var revokesFirst = List(_second, Now.AddMinutes(-4), Now.AddDays(7), keys: [new RevokedKey(_first.KeyId, null)]);

        foreach (var lists in new[] { new[] { fromFirst, revokesFirst }, [revokesFirst, fromFirst] })
        {
            var ledger = HostLedger.Load(Path.Combine(_root, $"order-{lists[0].Length}.json"));
            var policy = Revocations.ApplyAll(Policy(), lists, Now, ledger, out var notApplied);
            policy.RevokedDigests.Should().Contain(digest, "a rotated revoker's list still denies for the run (seventh review)");
            policy.RevokedKeys.Should().Contain(k => k.KeyId == _first.KeyId);
            ledger.RevokedDigests.Should().NotContain(digest, "and is not kept");
            notApplied.Should().ContainSingle();
        }
    }

    [Fact]
    public void A_list_never_extends_a_policy_s_own_expiry_however_the_policy_was_made()
    {
        // Seventh review: a policy built in code with only RevocationsExpire had it extended by a list.
        var policy = Policy() with { RevocationsExpire = Now.AddHours(1) };

        var applied = Revocations.ApplyAll(policy, [List(_first, Now.AddMinutes(-1), Now.AddDays(30)), List(_second, Now, Now.AddDays(30))], Now, null, out _);

        applied.RevocationsExpire.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void A_revoker_revoked_by_a_rotated_revoker_does_not_come_back_unless_a_live_revoker_carries_it_forward()
    {
        // Eighth review (high): the first revoker revoked a compromised third; then the first was rotated out. Its list no
        // longer decides about revokers, so the third would be live again on a fresh host and could take it over.
        var third = PackKey.Create();
        var policy = Policy() with
        {
            Keys = [.. Policy().Keys, new TrustedKey(third.KeyId, third.PublicKeyPem, ["io.example"], [Revocations.Role], null, null)],
            RevokedKeys = [new RevokedKey(_first.KeyId, null)],
        };
        var firstRevokesThird = List(_first, Now.AddDays(-2), Now.AddDays(7), keys: [new RevokedKey(third.KeyId, null)]);
        var thirdRevokesSecond = List(third, Now.AddMinutes(-1), Now.AddYears(10), keys: [new RevokedKey(_second.KeyId, null)]);

        FluentActions.Invoking(() => Revocations.ApplyAll(policy, [firstRevokesThird, thirdRevokesSecond], Now, null, out _))
            .Should().Throw<InvalidDataException>().WithMessage("*carry that revocation forward*");

        // Carried forward by the second, a live revoker, on a host with a ledger: the third is revoked and kept.
        var carried = List(_second, Now.AddMinutes(-2), Now.AddDays(7), keys: [new RevokedKey(third.KeyId, null)]);
        var ledger = HostLedger.Load(LedgerPath);
        var applied = Revocations.ApplyAll(policy, [firstRevokesThird, carried], Now, ledger, out _);
        applied.RevokedKeys.Should().Contain(k => k.KeyId == third.KeyId).And.NotContain(k => k.KeyId == _second.KeyId);
        ledger.RevokedKeys.Should().ContainKey(third.KeyId);
        // Later the compromised third's own list arrives: it decides nothing, and while it names a live revoker the run is
        // refused (fail closed) until it is dropped from the feed.
        FluentActions.Invoking(() => Revocations.ApplyAll(policy, [thirdRevokesSecond], Now, ledger, out _))
            .Should().Throw<InvalidDataException>().WithMessage("*has not expired*");
        ledger.RevokedKeys.Should().NotContainKey(_second.KeyId);
    }

    [Fact]
    public void A_revoker_cannot_dodge_the_carry_forward_refusal_by_revoking_itself_or_by_co_signing()
    {
        // Ninth review: the third, named by the rotated-out first, signs a list revoking the second and itself (F2), or adds
        // its signature to the second's list that revokes it (F3). A list revokes a key against its will only when a live
        // signer other than that key signed it.
        var third = PackKey.Create();
        var policy = Policy() with
        {
            Keys = [.. Policy().Keys, new TrustedKey(third.KeyId, third.PublicKeyPem, ["io.example"], [Revocations.Role], null, null)],
            RevokedKeys = [new RevokedKey(_first.KeyId, null)],
        };
        var firstRevokesThird = List(_first, Now.AddDays(-2), Now.AddDays(7), keys: [new RevokedKey(third.KeyId, null)]);
        var dodge = List(third, Now.AddMinutes(-1), Now.AddYears(10), keys: [new RevokedKey(_second.KeyId, null), new RevokedKey(third.KeyId, null)]);
        FluentActions.Invoking(() => Revocations.ApplyAll(policy, [firstRevokesThird, dodge], Now, null, out _))
            .Should().Throw<InvalidDataException>().WithMessage("*carry that revocation forward*");

        var twoLive = Policy() with { Keys = [.. Policy().Keys, new TrustedKey(third.KeyId, third.PublicKeyPem, ["io.example"], [Revocations.Role], null, null)] };
        var secondRevokesThird = List(_second, Now.AddMinutes(-2), Now.AddDays(7), keys: [new RevokedKey(third.KeyId, null)]);
        var thirdRevokesSecond = List(third, Now.AddMinutes(-1), Now.AddYears(10), keys: [new RevokedKey(_second.KeyId, null)]);
        FluentActions.Invoking(() => Revocations.ApplyAll(twoLive, [CoSignedBy(secondRevokesThird, third), thirdRevokesSecond], Now, null, out _))
            .Should().Throw<InvalidDataException>("co-signing its own revocation earns the third nothing: the run is refused, as before it was accepted");
    }

    [Fact]
    public void A_successor_not_yet_valid_does_not_escape_the_carry_forward_refusal()
    {
        // Ninth review (F1): a successor provisioned ahead, with a future notBefore, was not "valid now" and slipped through.
        var policy = Policy() with
        {
            Keys = [.. Policy().Keys.Select(k => k.KeyId == _second.KeyId ? k with { NotBefore = Now.AddDays(30) } : k)],
            RevokedKeys = [new RevokedKey(_first.KeyId, null)],
        };

        FluentActions.Invoking(() => Revocations.ApplyAll(policy, [List(_first, Now.AddDays(-1), Now.AddDays(7), keys: [new RevokedKey(_second.KeyId, null)])], Now, null, out _))
            .Should().Throw<InvalidDataException>().WithMessage("*has not expired*");
    }

    // A revocation document with another revoker's signature added: the same payload, a second signature entry.
    private static byte[] CoSignedBy(byte[] document, PackKey other)
    {
        var envelope = JsonNode.Parse(document)!.AsObject();
        var payload = Convert.FromBase64String(envelope["payload"]!.GetValue<string>());
        using var key = ECDsa.Create();
        key.ImportFromPem(other.PrivateKeyPem);
        var signature = key.SignData(Dsse.Pae(Revocations.PayloadType, payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        envelope["signatures"]!.AsArray().Add(new JsonObject { ["keyid"] = other.KeyId, ["sig"] = Convert.ToBase64String(signature) });
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    [Fact]
    public void A_retiring_revoker_s_final_list_given_twice_or_reissued_is_no_circle()
    {
        var final = List(_first, Now.AddMinutes(-5), Now.AddDays(7), keys: [new RevokedKey(_first.KeyId, null)]);
        var reissued = List(_first, Now.AddMinutes(-4), Now.AddDays(7), keys: [new RevokedKey(_first.KeyId, null)]);

        Revocations.ApplyAll(Policy(), [final, final], Now, null, out _).RevokedKeys.Should().Contain(k => k.KeyId == _first.KeyId);
        Revocations.ApplyAll(Policy(), [final, reissued], Now, null, out _).RevokedKeys.Should().Contain(k => k.KeyId == _first.KeyId);
    }

    [Fact]
    public void A_revoker_key_holds_that_role_alone()
    {
        var json = $$"""{"format":"agentmemory.pack.trust/1.1","keys":[{"keyId":"{{_first.KeyId}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(_first.PublicKeyPem)}},"idPrefixes":["io.example"],"roles":["publisher","revoker"]}]}""";

        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json))).Should().Throw<InvalidDataException>().WithMessage("*role alone*");
    }

    [Fact]
    public void Lists_that_revoke_each_other_s_signers_are_refused()
    {
        var one = List(_first, Now.AddMinutes(-5), Now.AddDays(7), keys: [new RevokedKey(_second.KeyId, null)]);
        var other = List(_second, Now.AddMinutes(-5), Now.AddDays(7), keys: [new RevokedKey(_first.KeyId, null)]);

        FluentActions.Invoking(() => Revocations.ApplyAll(Policy(), [one, other], Now, null, out _))
            .Should().Throw<InvalidDataException>().WithMessage("*circle*");
    }

    [Fact]
    public void A_signature_is_strict_DER_or_it_does_not_count()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(_sample.Key.PrivateKeyPem);
        byte[] padded;
        do padded = key.SignData("payload"u8, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        while (padded[3] != 33 && padded[5 + padded[3]] != 33);
        Dsse.IsStrictDer(padded).Should().BeTrue();
        Dsse.IsStrictDer(Unpadded(padded)).Should().BeFalse("an INTEGER without its sign pad is negative");

        byte[] Der(byte[] r, byte[] s) => [0x30, (byte)(4 + r.Length + s.Length), 0x02, (byte)r.Length, .. r, 0x02, (byte)s.Length, .. s];
        var one = new byte[] { 1 };
        Dsse.IsStrictDer(Der(one, one)).Should().BeTrue();
        Dsse.IsStrictDer(Der([0], one)).Should().BeFalse("r is zero");
        Dsse.IsStrictDer(Der([0, 1], one)).Should().BeFalse("r is not minimal");
        Dsse.IsStrictDer(Der([0, .. Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551")], one)).Should().BeFalse("r equals n");
        Dsse.IsStrictDer([.. Der(one, one), 0]).Should().BeFalse("bytes after the sequence");

        // And the verifier: the same signed pack, one signature's pad dropped, is refused.
        var files = _sample.Signed();
        var path = files.Keys.Single(PackPaths.IsSignature);
        var envelope = JsonNode.Parse(files[path])!.AsObject();
        var entry = envelope["signatures"]![0]!.AsObject();
        var signature = Convert.FromBase64String(entry["sig"]!.GetValue<string>());
        if (signature[3] == 33 || signature[5 + signature[3]] == 33)
        {
            entry["sig"] = Convert.ToBase64String(Unpadded(signature));
            files[path] = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            _sample.Verify(files).Problems.Should().Contain(p => p.Contains("not strict DER"));
        }
    }

    // The signature with the sign pad of r (or else s) dropped, lengths fixed: the encoding .NET alone used to accept.
    private static byte[] Unpadded(byte[] der)
    {
        var rLength = der[3];
        var r = der[4..(4 + rLength)];
        var s = der[(6 + rLength)..];
        if (r.Length == 33) r = r[1..]; else s = s[1..];
        return [0x30, (byte)(4 + r.Length + s.Length), 0x02, (byte)r.Length, .. r, 0x02, (byte)s.Length, .. s];
    }

    [Fact]
    public void Two_runs_saving_the_ledger_at_once_lose_nothing()
    {
        var first = HostLedger.Load(LedgerPath);
        var second = HostLedger.Load(LedgerPath);
        first.Record(["sha256:" + new string('a', 64)], [new RevokedKey(_first.KeyId, null)]);
        first.Save(LedgerPath);
        second.Record("io.example.notes", "1.0.0", "sha256:" + new string('b', 64));
        second.Save(LedgerPath);

        var merged = HostLedger.Load(LedgerPath);
        merged.RevokedDigests.Should().Contain("sha256:" + new string('a', 64), "the stale run's save keeps what the other saved");
        merged.Refusal("io.example.notes", "1.0.0", "sha256:" + new string('c', 64)).Should().Contain("other content");
    }

    [Theory]
    [InlineData("""{"format":"agentmemory.pack.ledger/1.1","packs":{},"revoked":{"digests":[],"keys":[]},"revoked":{"digests":[],"keys":[]}}""", "repeat")]
    [InlineData("""{"format":"agentmemory.pack.ledger/1.1","packs":{"io.example.notes":{"highest":"9.0","versions":{}}}}""", "not SemVer")]
    [InlineData("""{"format":"agentmemory.pack.ledger/1.1","packs":{},"revocations":{}}""", "earlier preview build")]
    public void A_ledger_this_host_cannot_read_whole_is_refused(string json, string reason)
    {
        File.WriteAllText(LedgerPath, json);

        FluentActions.Invoking(() => HostLedger.Load(LedgerPath)).Should().Throw<InvalidDataException>().Which.Message.Should().ContainEquivalentOf(reason);
    }
}
