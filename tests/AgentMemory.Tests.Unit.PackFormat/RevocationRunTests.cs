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
        retired.RetireRevocationLists();
        Stale(VerifyAt(Now.AddDays(5), Policy(), retired)).Should().BeFalse("a host that stops using lists forgets their freshness");
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

        notApplied.Should().ContainSingle().Which.Should().Contain("not applied");
        policy.RevokedDigests.Should().NotContain(digest);
        next.RevokedDigests.Should().NotContain(digest, "nothing it said is kept");
        FluentActions.Invoking(() => Revocations.Apply(Policy(), fromRevoked, Now, HostLedger.Load(LedgerPath))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Within_one_run_a_list_whose_signer_another_revokes_does_not_count_whatever_the_order()
    {
        var digest = "sha256:" + new string('d', 64);
        var fromFirst = List(_first, Now.AddMinutes(-5), Now.AddDays(7), digests: [digest]);
        var revokesFirst = List(_second, Now.AddMinutes(-4), Now.AddDays(7), keys: [new RevokedKey(_first.KeyId, null)]);

        foreach (var lists in new[] { new[] { fromFirst, revokesFirst }, [revokesFirst, fromFirst] })
        {
            var policy = Revocations.ApplyAll(Policy(), lists, Now, null, out var notApplied);
            policy.RevokedDigests.Should().NotContain(digest);
            policy.RevokedKeys.Should().Contain(k => k.KeyId == _first.KeyId);
            notApplied.Should().ContainSingle();
        }
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
