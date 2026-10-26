using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Findings of the independent design-against-code review (PACK-review, 26 Oct), each fixed and held here: ids that the
/// format accepts are module ids too, with no line feed slipping past an anchor; integers beyond 64 bits; unlisted junk under
/// META/signatures/; malformed trust policies and ledgers refused with a reason instead of a crash.
/// </summary>
public sealed class ReviewFindingsTests
{
    [Theory]
    [InlineData("io.example.notes\n", "a line feed after the id")]
    [InlineData("io.notes", "two segments: not a module id")]
    [InlineData("io.example.3d-notes", "a segment that starts with a digit")]
    [InlineData("io.example.notes-of-a-very-long-name-that-goes-on-and-on-past-sixty-four", "longer than 64")]
    public void An_id_the_host_could_not_load_is_refused_by_the_format(string id, string why)
    {
        PackManifest.IsValidId(id).Should().BeFalse(why);
        PackManifest.IsValidId("io.example.notes").Should().BeTrue();
    }

    [Fact]
    public void A_version_or_a_path_with_a_trailing_line_feed_is_refused()
    {
        SemVer.TryParse("1.0.0\n", out _).Should().BeFalse();
        PackPaths.Problems(["records/note.jsonl\n"]).Should().NotBeEmpty();
    }

    [Fact]
    public void An_integer_beyond_64_bits_is_refused_like_one_beyond_2_to_the_53()
    {
        PackJson.Check(Encoding.UTF8.GetBytes("{\"n\":99999999999999999999}")).Should().Contain("2^53");
        PackJson.Check(Encoding.UTF8.GetBytes("{\"n\":1.5}")).Should().BeNull();
    }

    [Fact]
    public void A_file_under_signatures_that_is_not_an_envelope_refuses_the_pack_even_when_the_signature_holds()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        sample.Verify(files).IsVerified.Should().BeTrue();

        files["META/signatures/payload.bin"] = [0x4D, 0x5A, 0x90, 0x00];

        sample.Verify(files).Problems.Should().ContainSingle().Which.Should().Contain("META/signatures/payload.bin");
    }

    [Theory]
    [InlineData("""{"format":"agentmemory.pack.trust/1.1","keys":[{"keyId":"x","idPrefixes":["io.example"],"roles":["publisher"]}]}""")]
    [InlineData("""{"format":"agentmemory.pack.trust/1.1","revoked":{"expires":"soon"}}""")]
    public void A_malformed_trust_policy_is_refused_with_a_reason(string json)
    {
        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(json))).Should().Throw<InvalidDataException>().WithMessage("*malformed*");
    }

    [Fact]
    public void A_malformed_ledger_is_refused_with_a_reason()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"format":"agentmemory.pack.ledger/1.1","packs":{"io.example.notes":{"versions":{}}}}""");
            FluentActions.Invoking(() => HostLedger.Load(path)).Should().Throw<InvalidDataException>().WithMessage("*malformed*");
        }
        finally { File.Delete(path); }
    }
}
