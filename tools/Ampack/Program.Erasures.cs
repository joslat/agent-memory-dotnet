// SPDX-License-Identifier: Apache-2.0
using AgentMemory.Packs.Format;

namespace Ampack;

/// <summary>
/// Erasures (feature 08): <c>ampack erase</c> builds and signs one; <c>ampack erasure verify</c> checks one, inside a pack's
/// attestations or a file, by a key the trust policy holds as publisher or revoker for the pack's id; <c>ampack erasure
/// receipt</c> signs a host's receipt that it applied one, naming nothing else.
/// </summary>
internal static partial class Program
{
    private static int ErasureCommand(Options o) => o.Positional(0, "erasure action (verify, receipt)") switch
    {
        "verify" => ErasureVerify(o),
        "receipt" => ErasureReceipt(o),
        var other => throw new UsageException($"Unknown erasure action '{other}': verify, receipt."),
    };

    // ampack erase <pack id> --erased Type=key [--erased ...] [--reason withdrawn] [--plain-keys] --key <publisher.pem> --out <file>
    private static int Erase(Options o)
    {
        var id = o.Positional(0, "pack id");
        if (!PackManifest.IsValidId(id)) throw new UsageException($"'{Shown(id, 100)}' is not a reverse-DNS pack id.");
        var records = o.Values("erased").Select(r => r.Split('=', 2) is [var type, var key] && type.Length > 0 && key.Length > 0
            ? (Type: type, Key: key) : throw new UsageException("--erased is Type=key.")).ToList();
        if (records.Count == 0) throw new UsageException("Name at least one --erased Type=key.");
        var reason = o.Value("reason") ?? "withdrawn";
        using var signer = Signer(o);
        var envelope = PackErasure.Create(id, records, reason, DateTimeOffset.UtcNow, signer, o.Flag("plain-keys"));
        var output = o.Value("out") ?? throw new UsageException("Name the erasure file with --out.");
        File.WriteAllBytes(output, envelope);
        Console.WriteLine($"Wrote an erasure of {records.Count} record(s) of {id} ({reason}){(o.Flag("plain-keys") ? "" : ", keys as salted hashes")}: {output}");
        return 0;
    }

    // ampack erasure receipt <erasure file> --key <host.pem> --out <file>: a host's receipt that it applied the erasure.
    private static int ErasureReceipt(Options o)
    {
        var erasure = PackReader.ReadFile(o.Positional(1, "erasure file"), Dsse.MaximumEnvelopeBytes, "The erasure");
        // Only for an erasure this host's trust verifies (review F08, M5: any envelope was receipted as applied).
        if (PackErasure.Verify(erasure, null, Policy(o), DateTimeOffset.UtcNow) is { Erasure: null, Problems: var refusals }) return Report(refusals, "");
        using var signer = Signer(o);
        var output = o.Value("out") ?? throw new UsageException("Name the receipt file with --out.");
        File.WriteAllBytes(output, PackErasure.Receipt(erasure, DateTimeOffset.UtcNow, signer));
        Console.WriteLine($"Wrote a receipt that this host applied {PackCertificate.IdentityOf(erasure)}: {output}");
        return 0;
    }

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
