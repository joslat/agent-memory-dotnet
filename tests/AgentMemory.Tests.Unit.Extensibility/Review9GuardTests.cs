#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Review 9's findings, each guarded where the rule lives; eng/guards/mutants.json records the breakage each must catch.</summary>
public sealed class Review9GuardTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    private LoadedPack Pack() => PackLoader.Load(PackAuthoring.Build(new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(
        Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal),
        _key.PrivateKeyPem), PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory"))));

    private static PackEvaluation Certificate(string digest, bool passed, bool trusted = true) => new()
    {
        Digest = "sha256:" + digest.PadRight(64, '0'), File = $"META/attestations/{digest}.certificate.dsse.json", Trusted = trusted, Passed = passed,
        EvaluatedAt = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), Evaluator = "Lab",
    };

    [Fact]
    public void C_H1_copies_of_one_pack_pool_what_travels_outside_the_digest_whatever_their_order()
    {
        // One pack reached twice: one copy carries a trusted pass, the other a trusted failure of the same second and an extra
        // signer. The first copy's certificates were kept, so `ampack gate P F` switched the pack on and `gate F P` kept it off.
        var pack = Pack();
        var passing = pack with { Certificates = [Certificate("a", passed: true)] };
        var failing = pack with { Certificates = [Certificate("b", passed: false)], Signers = [.. pack.Signers, "assurer 0123456789abcdef"] };

        var first = PackMountPlan.Of([passing, failing]).Single();
        var second = PackMountPlan.Of([failing, passing]).Single();

        first.Certificates.Select(c => c.Digest).Should().Equal(second.Certificates.Select(c => c.Digest)).And.HaveCount(2);
        first.Signers.Should().Equal(second.Signers).And.Contain("assurer 0123456789abcdef");
        PackCertificates.LatestTrusted(first)!.Passed.Should().BeFalse("a trusted failure of the same second is never hidden by a pass");
        PackCertificates.LatestTrusted(second)!.Passed.Should().BeFalse();

        // Copies of one statement, trusted in one copy only: trusted, whichever copy came first.
        var untrusted = pack with { Certificates = [Certificate("c", passed: true, trusted: false)] };
        var trusted = pack with { Certificates = [Certificate("c", passed: true)] };
        PackMountPlan.Of([untrusted, trusted]).Single().Certificates.Single().Trusted.Should().BeTrue("adding a copy never takes trust away");
        PackMountPlan.Of([trusted, untrusted]).Single().Certificates.Single().Trusted.Should().BeTrue();
    }
}
