using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// A host's trial aggregate (feature 13, R-CORE-140): what a publisher, a hub or a licensor may learn of a trial without the host's
/// history. Built from a decision, it holds the counts, bounds and verdicts per target, the number of tasks and the window, and
/// copies no text from the decision: only its digest, the pack's id, version and digest, and numbers.
/// </summary>
[Experimental("AMEXT001")]
public static class PackAggregates
{
    /// <summary>
    /// Builds and signs the aggregate of <paramref name="decision"/> for <paramref name="pack"/>. With <paramref name="epsilonMilli"/>,
    /// each count carries integer discrete-Laplace noise of that epsilon (in thousandths), drawn from the host's cryptographic
    /// random source and never stored, and no bound is stated. The host shares it only with the operator's consent.
    /// </summary>
    /// <exception cref="ArgumentException">The decision is not for this pack, or epsilonMilli is outside 1 to 100,000.</exception>
    public static byte[] Create(LoadedPack pack, PackActivation decision, string signingKeyPem, int? epsilonMilli = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.ModuleId != pack.Id || decision.PackDigest != pack.Digest)
            throw new ArgumentException($"The decision is for {decision.ModuleId} ({decision.PackDigest ?? "compiled code"}), not {pack.Id} ({pack.Digest}).", nameof(decision));
        if (epsilonMilli is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(epsilonMilli), "epsilonMilli is 1 to 100,000.");
        var with = decision.With ?? throw new ArgumentException("The decision carries no measurement.", nameof(decision));
        var noisy = epsilonMilli is not null;
        long Noise(long count) => noisy ? Math.Max(0, count + DiscreteLaplace(epsilonMilli!.Value / 1000.0)) : count;
        var checks = decision.Checks.Select(c =>
        {
            // The counts each target rests on: rates over tasks, precision over items, no-harm over abstention tasks; a latency, a
            // safety rate or a cost has none here.
            var (successes, trials) = c.Target switch
            {
                "hitRate" or "lift" => (with.Hits, with.Tasks),
                "precision" or "precisionLift" => (with.Correct, with.Offered),
                "noHarm" => (with.Quiet ?? 0, with.Abstentions ?? 0),
                _ => (0, 0),
            };
            return new AggregateCheck(c.Target, Noise(successes), Noise(trials), Micro(c.Required), noisy ? null : Micro(c.Observed), c.Met);
        }).ToList();
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(PackActivationFolder.Statement(decision))).ToLowerInvariant();
        var aggregate = new PackAggregate(pack.Id, pack.Version, pack.Digest, pack.Manifest.Trial?.Measure ?? "test-impact", with.Tasks,
            from ?? decision.DecidedAt, to ?? decision.DecidedAt, noisy ? "discrete-laplace" : "none", epsilonMilli, digest, checks, checks.All(c => c.Met));
        using var signer = PackSigner.FromPem(signingKeyPem);
        return PackAggregate.Create(aggregate, signer);
    }

    private static long Micro(double value) => checked((long)Math.Round(value * 1_000_000));

    // Two-sided geometric noise: the difference of two geometric draws with p = 1 - e^-epsilon, from a uniform in (0, 1].
    private static long DiscreteLaplace(double epsilon)
    {
        long Geometric()
        {
            var u = (RandomNumberGenerator.GetInt32(1, int.MaxValue) + 1.0) / int.MaxValue;
            return (long)Math.Floor(Math.Log(u) / -epsilon);
        }
        return Geometric() - Geometric();
    }
}
