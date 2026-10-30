using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The pack card (v1.2 §15) and evaluation certificates (v1.2 §16): the card is a listed, signed file read strictly; a
/// certificate lives in <c>META/attestations/</c>, outside the files list, signed by a key a host names as evaluator, for
/// exactly one pack, with the AEF run it names when that run travels beside it.
/// </summary>
public sealed class CertificateTests
{
    private static readonly DateTimeOffset EvaluatedAt = SamplePack.SignedAt.AddMinutes(30);
    private readonly PackKey _evaluator = PackKey.Create();

    [Fact]
    public void A_certificate_travels_outside_the_files_list_and_the_builder_keeps_it_only_while_the_list_is_unchanged()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        var digest = sample.Verify(files).Pack!.Digest;
        var (path, envelope) = Certificate(sample, files);
        files[path] = envelope;

        var verified = sample.Verify(files).Pack!;

        verified.Digest.Should().Be(digest);
        verified.Attestations.Keys.Should().Equal(path);
        FilesList.IsListed(path).Should().BeFalse();
        PackBuilder.Build(files).Files.Should().ContainKey(path, "the same content keeps what was said about it");
        var changed = new Dictionary<string, byte[]>(files) { ["records/note.jsonl"] = SamplePack.Utf8("{\"key\":\"n1\",\"title\":\"First\",\"text\":\"Changed\"}\n") };
        var (rebuilt, dropped) = PackBuilder.Build(changed);
        rebuilt.Should().NotContainKey(path, "a certificate of other content says nothing about this one");
        dropped.Should().BeTrue();
    }

    [Fact]
    public void A_certificate_verifies_for_its_pack_by_a_key_named_evaluator_and_reads_back_what_was_signed()
    {
        var sample = new SamplePack();
        var files = sample.Signed();
        var pack = sample.Verify(files).Pack!;
        var (_, envelope) = Certificate(sample, files);

        var (certificate, signedBy, problems) = PackCertificate.Verify(envelope, pack, Policy(sample), SamplePack.Now);

        problems.Should().BeEmpty();
        signedBy.Should().Be(_evaluator.KeyId);
        certificate!.Evaluator.Should().Be("Example Lab");
        certificate.EvaluatedAt.Should().Be(EvaluatedAt);
        certificate.Checks.Should().Equal(new CertificateCheck("hitRate", 500, 600, true, null));
        certificate.With.Should().BeEquivalentTo(ArmOf(40, 24, 120, 30, 12_500));
        certificate.Statistic.Should().Be("agentmemory.trial.hedged/3");
        certificate.Passed.Should().BeTrue();

        PackCertificate.Verify(envelope, pack, sample.Policy(), SamplePack.Now).Problems.Should().Contain(p => p.Contains("not in the trust policy"));
        PackCertificate.Verify(envelope, pack, Policy(sample, prefix: "io.other"), SamplePack.Now).Certificate.Should().BeNull();
        PackCertificate.Verify(envelope, pack, Policy(sample, role: "assurer"), SamplePack.Now).Certificate.Should().BeNull();
        var other = new SamplePack();
        PackCertificate.Verify(envelope, other.Verify(other.Signed("{\"key\":\"n2\",\"title\":\"Second\",\"text\":\"x\"}\n")).Pack!, Policy(other), SamplePack.Now)
            .Problems.Should().Contain(p => p.Contains("not this pack"));
    }

    [Fact]
    public void Signing_refuses_a_certificate_whose_verdict_or_text_breaks_the_format()
    {
        var sample = new SamplePack();
        using var signer = PackSigner.FromPem(_evaluator.PrivateKeyPem);

        FluentActions.Invoking(() => Sample(sample, passed: true, met: false).Sign(signer)).Should().Throw<InvalidDataException>().WithMessage("*passed*");
        FluentActions.Invoking(() => Sample(sample, tasks: 9).Sign(signer)).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => Sample(sample, evaluator: "Example\nLab").Sign(signer)).Should().Throw<InvalidDataException>().WithMessage("*evaluator*");
        FluentActions.Invoking(() => Sample(sample, evaluator: "Example\u001b[2JLab").Sign(signer)).Should().Throw<InvalidDataException>();
        Sample(sample, passed: false, met: false).Sign(signer).Should().NotBeEmpty("an honest failure is a valid certificate");
    }

    [Fact]
    public void An_evaluator_key_holds_that_role_alone()
    {
        var sample = new SamplePack();
        var policy = $$"""
            {"format":"agentmemory.pack.trust/1.1","keys":[{"keyId":"{{_evaluator.KeyId}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(_evaluator.PublicKeyPem)}},"idPrefixes":["io.example"],"roles":["evaluator","publisher"]}]}
            """;

        FluentActions.Invoking(() => TrustPolicy.Parse(Encoding.UTF8.GetBytes(policy))).Should().Throw<InvalidDataException>().WithMessage("*evaluator key holds that role alone*");
        TrustPolicy.Parse(Encoding.UTF8.GetBytes(policy.Replace(",\"publisher\"", "", StringComparison.Ordinal))).Keys.Should().ContainSingle();
        // And never signs a pack: a pack signature names a pack role.
        var files = sample.Signed();
        FluentActions.Invoking(() => PackBuilder.Sign(files, _evaluator.PrivateKeyPem, "evaluator", SamplePack.SignedAt)).Should().Throw<Exception>();
    }

    [Fact]
    public void The_attestations_area_holds_certificates_and_their_runs_only_within_bounds()
    {
        const string stem = "0123456789abcdef-20261026t123000z";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"META/attestations/{stem}.certificate.dsse.json"] = [],
            [$"META/attestations/{stem}.aef/run.json"] = [],
        };
        PackAttestations.Problems(files).Should().BeEmpty();

        PackAttestations.Problems(new Dictionary<string, byte[]>(files) { ["META/attestations/readme.txt"] = [] }).Should().ContainSingle().Which.Should().Contain("not an attestation");
        PackAttestations.Problems(new Dictionary<string, byte[]> { [$"META/attestations/{stem}.aef/run.json"] = [] }).Should().ContainSingle().Which.Should().Contain("without its certificate");
        PackAttestations.Problems(new Dictionary<string, byte[]>(files) { [$"META/attestations/{stem}.aef/big.bin"] = new byte[(8 << 20) + 1] })
            .Should().ContainSingle().Which.Should().Contain("at most 256 files");
        var nine = Enumerable.Range(0, 9).ToDictionary(i => $"META/attestations/{i:x16}-20261026t123000z.certificate.dsse.json", _ => Array.Empty<byte>());
        PackAttestations.Problems(nine).Should().ContainSingle().Which.Should().Contain("at most 8");
        PackAttestations.StemFor(new string('a', 64), new DateTimeOffset(2026, 10, 26, 12, 30, 0, TimeSpan.FromHours(2))).Should().Be("aaaaaaaaaaaaaaaa-20261026t103000z");
    }

    [Fact]
    public void A_run_hash_is_AEF_s_own_over_every_sealed_file_in_path_order()
    {
        var run = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["run.json"] = SamplePack.Utf8("{\"runId\":\"r1\"}\n"),
            ["results/b.ndjson"] = SamplePack.Utf8("b\n"),
            ["results/a.ndjson"] = SamplePack.Utf8("a\n"),
            ["seal.json"] = SamplePack.Utf8("{}"),
            ["attestation.dsse.json"] = SamplePack.Utf8("{}"),
            ["overlays/judge.json"] = SamplePack.Utf8("{}"),
        };
        var manifest = string.Concat(new[] { "results/a.ndjson", "results/b.ndjson", "run.json" }
            .Select(p => $"{FilesList.Hex(run[p])}  {run[p].Length}  {p}\n"));

        PackAttestations.RunHash(run).Should().Be(FilesList.Hex(Encoding.UTF8.GetBytes(manifest)));
        PackAttestations.RunHash(new Dictionary<string, byte[]>(run) { ["seal.json"] = SamplePack.Utf8("{\"other\":1}") }).Should().Be(PackAttestations.RunHash(run), "the seal is outside what it seals");
        PackAttestations.RunId(run).Should().Be("r1");
        PackAttestations.RunId(new Dictionary<string, byte[]> { ["run.json"] = SamplePack.Utf8("{\"runId\":\"r1\",\"runId\":\"r2\"}") }).Should().BeNull("a repeated key is outside the JSON profile");
    }

    [Theory]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"x","limitations":"y"}""", null)]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A","contact":"a@example.com"},"intendedUse":"x\nmore\tlines","limitations":"y","boundaries":["b"],"risks":["r"],"provenance":"p","audience":"a","domain":"d","references":["https://example.com"]}""", null)]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"x"}""", "needs 'limitations'")]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":"A","intendedUse":"x","limitations":"y"}""", "owner")]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A","email":"a"},"intendedUse":"x","limitations":"y"}""", "owner")]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"","limitations":"y"}""", "intendedUse")]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"x","limitations":"y","risks":["r","r"]}""", "risks")]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"x","limitations":"y","references":[1]}""", "references")]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"x","limitations":"y","domain":"d\u0007"}""", "domain")]
    [InlineData("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"x","limitations":"y","rating":5}""", "unknown field 'rating'")]
    [InlineData("""["agentmemory.pack.card/1"]""", "not an object")]
    public void A_card_is_read_strictly(string json, string? problem)
    {
        var (card, found) = PackCard.Parse(Encoding.UTF8.GetBytes(json));

        if (problem is null)
        {
            found.Should().BeNull();
            card!.OwnerName.Should().Be("A");
        }
        else found.Should().Contain(problem);
    }

    [Fact]
    public void A_card_is_part_of_the_signed_content_and_a_malformed_one_refuses_the_pack()
    {
        var sample = new SamplePack();
        var source = sample.Source();
        source["card.json"] = SamplePack.Utf8("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"x","limitations":"y"}""" + "\n");
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        sample.Verify(files).IsVerified.Should().BeTrue();

        source["card.json"] = SamplePack.Utf8("""{"format":"agentmemory.pack.card/1","owner":{"name":"A"},"intendedUse":"x"}""" + "\n");
        (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        sample.Verify(files).Problems.Should().Contain(p => p.Contains("card.json needs 'limitations'"));
    }

    private (string Path, byte[] Envelope) Certificate(SamplePack sample, Dictionary<string, byte[]> files)
    {
        using var signer = PackSigner.FromPem(_evaluator.PrivateKeyPem);
        var digest = "sha256:" + FilesList.Hex(files[PackPaths.FilesList]);
        var envelope = (Sample(sample) with { PackDigest = digest }).Sign(signer);
        return (PackAttestations.CertificatePath(PackAttestations.StemFor(signer.KeyId, EvaluatedAt)), envelope);
    }

    private static PackCertificate Sample(SamplePack sample, bool passed = true, bool met = true, int tasks = 40, string evaluator = "Example Lab") =>
        new(sample.Id, sample.Version, "sha256:" + new string('0', 64), evaluator, "kit", "1.0.0", EvaluatedAt, "test-impact", "none", null, "the lab's own tasks",
            "agentmemory.trial.hedged/3", [new CertificateCheck("hitRate", 500, met ? 600 : 400, met, null)], ArmOf(tasks, Math.Min(24, tasks), 120, 30, 12_500),
            null, passed, null, null);

    // An arm with consistent per-task evidence: items spread evenly, one right item on each of the first `hits` tasks and the
    // rest of the right items on them too; the cap is the most any task offers.
    private static CertificateArm ArmOf(int tasks, int hits, int offered, int correct, long p95)
    {
        var o = Enumerable.Range(0, tasks).Select(i => offered / Math.Max(1, tasks) + (i < offered % Math.Max(1, tasks) ? 1 : 0)).ToArray();
        if (hits > tasks || correct < hits || correct > o.Take(hits).Sum() || o.Take(hits).Any(x => x == 0))
            throw new ArgumentException($"No arm of {tasks} tasks has {hits} hits and {correct} of {offered} items right.");
        var c = new int[tasks];
        for (var i = 0; i < hits; i++) c[i] = 1;
        for (var (left, i) = (correct - hits, 0); left > 0 && hits > 0; i = (i + 1) % hits)
            if (c[i] < o[i]) { c[i]++; left--; }
        return new(tasks, hits, offered, correct, p95, Math.Max(1, o.DefaultIfEmpty(0).Max()), 8, [.. o.Select((x, i) => new CertificateTask(x, c[i]))]);
    }


    private TrustPolicy Policy(SamplePack sample, string prefix = "io.example", string role = "evaluator") => new()
    {
        Keys =
        [
            new TrustedKey(sample.Key.KeyId, sample.Key.PublicKeyPem, ["io.example"], ["publisher"], null, null),
            new TrustedKey(_evaluator.KeyId, _evaluator.PublicKeyPem, [prefix], [role], null, null),
        ],
    };
}
