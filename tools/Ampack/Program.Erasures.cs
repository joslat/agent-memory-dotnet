using AgentMemory.Packs.Format;

namespace Ampack;

/// <summary>
/// Erasures (feature 08): <c>ampack erasure verify</c> checks one, inside a pack's attestations or a file, for the pack it names,
/// by a key the trust policy holds as publisher or revoker for the pack's id.
/// </summary>
internal static partial class Program
{
    private static int ErasureCommand(Options o) => o.Positional(0, "erasure action (verify)") switch
    {
        "verify" => ErasureVerify(o),
        var other => throw new UsageException($"Unknown erasure action '{other}': verify."),
    };

    // Verifies the pack, then the erasure named by --erasure (inside it under META/attestations/, or a file), or each it carries.
    private static int ErasureVerify(Options o)
    {
        var content = PackReader.Read(o.Positional(1, "pack.ampack"));
        var policy = Policy(o);
        var verification = PackVerifier.VerifyWhole(content, policy, DateTimeOffset.UtcNow);
        if (verification.Pack is not { } pack) return Report(verification.Problems, "");
        var chosen = o.Value("erasure");
        List<(string Name, byte[] Envelope)> erasures = [.. PackErasure.Carried(pack.Attestations)
            .Select(e => (Name: PackPaths.AttestationsFolder + e.Stem + PackErasure.Suffix, e.Envelope)).Where(e => chosen is null || e.Name == chosen)];
        if (chosen is not null && erasures.Count == 0)
        {
            if (chosen.StartsWith(PackPaths.AttestationsFolder, StringComparison.Ordinal)) return Report([$"The pack carries no {chosen}."], "");
            erasures.Add((chosen, PackReader.ReadFile(chosen, Dsse.MaximumEnvelopeBytes, "The erasure")));
        }
        if (erasures.Count == 0) return Report([$"{pack.Id} {pack.Version} carries no erasure."], "");
        var problems = new List<string>();
        foreach (var (name, envelope) in erasures)
        {
            var (erasure, signedBy, refusals) = PackErasure.Verify(envelope, pack.Id, policy, DateTimeOffset.UtcNow);
            if (erasure is null) { problems.AddRange(refusals.Select(r => $"{Shown(name, 200)}: {r}")); continue; }
            Console.WriteLine($"Verified the erasure of {erasure.Records.Count} record(s) of {pack.Id} ({erasure.Reason}), issued {erasure.IssuedAt:u} by {Dsse.Short(signedBy!)}.");
        }
        return Report(problems, "");
    }
}
