using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// <c>ampack verify --explain</c> (PACK-toolset): every step in order, what it checked and whether it held; explaining never
/// changes the verdict, and the step that failed is the one named.
/// </summary>
public sealed class ExplainTests
{
    private readonly SamplePack _sample = new();

    private (PackVerification Result, List<PackStep> Steps) Explained(Dictionary<string, byte[]> files)
    {
        var steps = new List<PackStep>();
        var result = PackVerifier.Verify(new PackContent(files, "test"), _sample.Policy(), SamplePack.Now, explain: steps.Add);
        return (result, steps);
    }

    [Fact]
    public void A_valid_pack_passes_every_step_in_order()
    {
        var (result, steps) = Explained(_sample.Signed());

        result.IsVerified.Should().BeTrue();
        // Records are parsed last, once the signatures hold (review 1, L3).
        steps.Select(s => s.Name).Should().Equal("paths", "files list", "content", "JSON profile", "pack.json and kind", "revocation and expiry", "signatures", "records");
        steps.Should().OnlyContain(s => s.Passed && s.Lines.Count > 0);
    }

    [Fact]
    public void A_changed_record_fails_at_content_and_goes_no_further()
    {
        var files = _sample.Signed();
        files["records/note.jsonl"] = SamplePack.Utf8("{\"key\":\"n1\",\"title\":\"First\",\"text\":\"Changed\"}\n");

        var (result, steps) = Explained(files);

        result.IsVerified.Should().BeFalse();
        steps.Last().Should().Match<PackStep>(s => s.Name == "content" && !s.Passed);
        steps.Last().Lines.Should().ContainSingle().Which.Should().Contain("records/note.jsonl");
    }

    [Fact]
    public void Explaining_never_changes_the_verdict()
    {
        var random = new Random(7);
        var archive = StrictZip.Write(_sample.Signed());
        for (var run = 0; run < 500; run++)
        {
            var bytes = archive.ToArray();
            bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            Dictionary<string, byte[]> files;
            try { files = StrictZip.Read(bytes, PackLimits.Default); }
            catch (InvalidDataException) { continue; }
            var plain = PackVerifier.Verify(new PackContent(files, "test"), _sample.Policy(), SamplePack.Now);
            var (explained, steps) = Explained(files);
            explained.IsVerified.Should().Be(plain.IsVerified);
            explained.Problems.Should().Equal(plain.Problems);
            steps.All(s => s.Passed).Should().Be(plain.IsVerified, $"run {run}: a refused pack names a failing step");
        }
    }
}
