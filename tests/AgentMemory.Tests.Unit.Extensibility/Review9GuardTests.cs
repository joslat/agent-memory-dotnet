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
    public void C_H1_copies_of_one_pack_are_held_by_the_host_s_rule_whatever_their_order()
    {
        // One pack reached twice: one copy carries a trusted pass, the other a trusted failure. The first copy's certificates were
        // kept, so `ampack gate P F` switched the pack on and `gate F P` kept it off; then the copies merged, a rule no host has.
        // A host adds a module once (round 11): both orders are refused, the same way.
        var pack = Pack();
        var passing = pack with { Certificates = [Certificate("a", passed: true)] };
        var failing = pack with { Certificates = [Certificate("b", passed: false)], Signers = [.. pack.Signers, "assurer 0123456789abcdef"] };

        foreach (var order in new[] { new[] { passing, failing }, [failing, passing] })
            FluentActions.Invoking(() => PackMountPlan.Of(order)).Should().Throw<PackLoadException>()
                .Which.Problems.Should().ContainSingle().Which.Should().Contain("given more than once");
        PackMountPlan.Of([passing]).Should().ContainSingle();
    }
}
