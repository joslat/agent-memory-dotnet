using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// PACK-harden (review 4.7) and PACK-conformance-v12: a signing time is the signer's own claim, so a key's expiry is checked
/// against now as well; and every time in a pack or a policy is an RFC 3339 date-time, as the Python verifier reads it.
/// </summary>
public sealed class HardenTrustTests
{
    [Fact]
    public void A_signature_back_dated_into_an_expired_keys_window_does_not_count()
    {
        var sample = new SamplePack();
        var files = sample.Signed(); // signed at SamplePack.SignedAt
        var key = sample.Policy().Keys.Single();
        var expiredSince = SamplePack.SignedAt.AddMinutes(30);
        var policy = new TrustPolicy { Keys = [key with { NotAfter = expiredSince }] };

        // At SignedAt + 1 h the key has expired, though the signature claims a time inside its window.
        var result = PackVerifier.VerifyOne(new PackContent(files, "test"), policy, SamplePack.Now);

        result.IsVerified.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Contains("expired at", StringComparison.Ordinal) && p.Contains("timestamp token", StringComparison.Ordinal));
        PackVerifier.VerifyOne(new PackContent(files, "test"), policy, SamplePack.SignedAt.AddMinutes(10)).IsVerified.Should().BeTrue("within the window it counts");
    }

    [Theory]
    [InlineData("2026-10-27T00:00:00Z", true)]
    [InlineData("2026-10-27T00:00:00.1234567Z", true)]
    [InlineData("2026-10-27T02:00:00+02:00", true)]
    [InlineData("2026-10-27", false)]
    [InlineData("2026-10-27 00:00:00Z", false)]
    [InlineData("27/10/2026", false)]
    [InlineData("2026-10-27T00:00:00", false)]
    [InlineData("2026-10-27T00:00:00.12345678Z", false)]
    public void A_time_is_an_RFC_3339_date_time(string text, bool valid) =>
        PackTime.TryParse(text, out _).Should().Be(valid);

    [Fact]
    public void A_policy_time_that_is_not_RFC_3339_makes_the_policy_malformed()
    {
        var key = new SamplePack().Key;
        var json = $$"""{"format":"agentmemory.pack.trust/1.1","keys":[{"keyId":"{{key.KeyId}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(key.PublicKeyPem)}},"idPrefixes":["io.example"],"roles":["publisher"],"notAfter":"next year"}]}""";

        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json))).Should().Throw<InvalidDataException>().WithMessage("*malformed*notAfter*");
    }
}
