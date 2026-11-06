using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Readable minors (v1.2 §2.1, review 1 M18): a reader of 1.2 reads a later 1.x as far as it knows it, ignoring the fields it
/// does not know unless the pack names them in <c>critical</c>; at its own minor it stays strict; a writer signs only the minor
/// it writes; and the slot reserved for trusted time (a token beside its envelope) is kept and not judged.
/// </summary>
public sealed class ReadableMinorTests
{
    private const string Rest = "\"id\":\"io.example.notes\",\"version\":\"1.0.0\",\"kind\":\"knowledge\"";

    private static (PackManifest? Manifest, string? Problem) Parse(string format, string more = "") =>
        PackManifest.Parse(Encoding.UTF8.GetBytes("{\"format\":\"" + format + "\"," + Rest + more + "}"));

    [Fact]
    public void A_later_minor_is_read_as_far_as_this_reader_knows_it()
    {
        var (manifest, problem) = Parse("agentmemory.pack/1.3", ""","timestamps":{"authority":"https://tsa.example/"},"homepage":"x" """);

        problem.Should().BeNull();
        manifest!.FormatMinor.Should().Be(3);
        manifest.IgnoredFields.Should().Equal("timestamps", "homepage");
        Parse("agentmemory.pack/1.999").Problem.Should().BeNull();
    }

    [Fact]
    public void A_critical_field_this_reader_does_not_know_refuses_at_any_minor()
    {
        Parse("agentmemory.pack/1.3", ""","timestamps":{},"critical":["timestamps"]""").Problem
            .Should().Contain("'timestamps' critical").And.Contain("does not understand");
        Parse("agentmemory.pack/1.2", ""","timestamps":{},"critical":["timestamps"]""").Problem
            .Should().Contain("unknown field 'timestamps'", "at its own minor every field is known, so an unknown one refuses first");
        Parse("agentmemory.pack/1.2", ""","critical":["kind","id"]""").Problem.Should().BeNull("a known field may be named critical");
    }

    [Theory]
    [InlineData(""","critical":[]""", "malformed 'critical'")]
    [InlineData(""","critical":"kind" """, "malformed 'critical'")]
    [InlineData(""","critical":[1]""", "malformed 'critical'")]
    [InlineData(""","critical":["kind","kind"]""", "twice")]
    [InlineData(""","critical":["validUntil"]""", "holds no such field")]
    [InlineData(""","critical":["critical"]""", "holds no such field")]
    public void A_malformed_critical_list_is_refused(string more, string why) =>
        Parse("agentmemory.pack/1.3", more).Problem.Should().Contain(why);

    [Fact]
    public void Too_many_critical_names_are_refused()
    {
        var names = string.Join(",", Enumerable.Range(0, PackManifest.MaximumCritical + 1).Select(i => $"\"f{i}\""));
        var fields = string.Concat(Enumerable.Range(0, PackManifest.MaximumCritical + 1).Select(i => $",\"f{i}\":1"));
        Parse("agentmemory.pack/1.3", fields + ",\"critical\":[" + names + "]").Problem.Should().Contain("malformed 'critical'");
    }

    [Theory]
    [InlineData("agentmemory.pack/1.1")]
    [InlineData("agentmemory.pack/1.0")]
    [InlineData("agentmemory.pack/1.02")]
    [InlineData("agentmemory.pack/1.1000")]
    [InlineData("agentmemory.pack/2.0")]
    [InlineData("agentmemory.pack/1.2.0")]
    [InlineData(@"agentmemory.pack/1.3\n")]
    [InlineData("agentmemory.pack/1.٣")]
    public void Earlier_minors_another_major_and_other_spellings_are_not_read(string format) =>
        Parse(format).Problem.Should().Contain("reads the later 1.x minors");

    [Fact]
    public void The_values_of_known_fields_stay_closed_at_a_later_minor()
    {
        Parse("agentmemory.pack/1.3").Problem.Should().BeNull();
        PackManifest.Parse(Encoding.UTF8.GetBytes("""{"format":"agentmemory.pack/1.3","id":"io.example.notes","version":"1.0.0","kind":"dataset"}"""))
            .Problem.Should().Contain("needs a kind");
        Parse("agentmemory.pack/1.3", ""","protection":"escrowed" """).Problem.Should().Contain("unknown protection");
        Parse("agentmemory.pack/1.3", ""","code":"E3" """).Problem.Should().Contain("unknown code tier");
    }

    [Fact]
    public void A_signed_pack_of_a_later_minor_verifies_and_says_what_it_ignored()
    {
        var sample = new SamplePack();
        var source = sample.Source();
        source["pack.json"] = SamplePack.Utf8("""{"format":"agentmemory.pack/1.3","id":"io.example.notes","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)","tier":"open","licence":"MIT","timestamps":{}}""");
        // Built as a 1.2 builder would, then the later pack.json put in place and signed: what a 1.3 writer ships.
        var (files, _) = PackBuilder.Build(sample.Source());
        files["pack.json"] = source["pack.json"];
        files[PackPaths.FilesList] = FilesList.Build("io.example.notes", "1.0.0", files);
        SamplePack.SignRaw(files, sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        var steps = new List<PackStep>();

        var verified = PackVerifier.VerifyOne(new PackContent(files, "test"), sample.Policy(), SamplePack.Now, explain: steps.Add);

        verified.Problems.Should().BeEmpty();
        steps.Single(s => s.Name == "pack.json and kind").Lines.Should().Contain(d => d.Contains("format 1.3") && d.Contains("ignored timestamps"));
    }

    [Fact]
    public void A_writer_signs_only_the_minor_it_writes()
    {
        var source = new SamplePack().Source();
        source["pack.json"] = SamplePack.Utf8("""{"format":"agentmemory.pack/1.3","id":"io.example.notes","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)"}""");

        FluentActions.Invoking(() => PackBuilder.Build(source)).Should().Throw<InvalidDataException>().WithMessage("*this writer writes agentmemory.pack/1.2*");
    }

    [Fact]
    public void A_timestamp_token_beside_its_envelope_is_kept_and_not_judged_and_signing_again_drops_it()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        var envelope = files.Keys.Single(PackPaths.IsSignature);
        files[envelope + PackPaths.TimestampSuffix] = new byte[300];

        sample.Verify(files).Problems.Should().BeEmpty();
        var beside = new Dictionary<string, byte[]>(files) { [PackPaths.SignaturesFolder + "other.dsse.json.tsr"] = new byte[300] };
        sample.Verify(beside).Problems.Should().Contain(p => p.Contains("beside no envelope"));
        var large = new Dictionary<string, byte[]>(files) { [envelope + PackPaths.TimestampSuffix] = new byte[Dsse.MaximumEnvelopeBytes + 1] };
        sample.Verify(large).Problems.Should().Contain(p => p.Contains("timestamp token is small"));

        // A new envelope by the same key replaces the old one, and the token over the old signature goes with it.
        PackBuilder.Sign(files, sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt.AddMinutes(1));
        files.Keys.Should().NotContain(envelope + PackPaths.TimestampSuffix);
    }
}
