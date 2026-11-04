// SPDX-License-Identifier: Apache-2.0
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
    /// Builds and signs the aggregate of <paramref name="decision"/> for <paramref name="pack"/>: exact counts. The host shares it
    /// only with the operator's consent. No noise is offered (review 11-13, H7): with exact verdicts beside the counts, counts that
    /// move by more than one per task and repeated releases, a discrete-Laplace epsilon did not give the privacy it named.
    /// </summary>
    /// <exception cref="ArgumentException">The decision is not for this pack, or checks a target an aggregate cannot carry.</exception>
    public static byte[] Create(LoadedPack pack, PackActivation decision, string signingKeyPem, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.ModuleId != pack.Id || decision.PackDigest != pack.Digest)
            throw new ArgumentException($"The decision is for {decision.ModuleId} ({decision.PackDigest ?? "compiled code"}), not {pack.Id} ({pack.Digest}).", nameof(decision));
        var with = decision.With ?? throw new ArgumentException("The decision carries no measurement.", nameof(decision));
        if (decision.Checks.FirstOrDefault(c => !PackRegistry.Targets.Contains(c.Target)) is { } other)
            throw new ArgumentException($"An aggregate carries the trial's targets only; this decision checks {other.Target}.", nameof(decision));
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
            return new AggregateCheck(c.Target, successes, trials, Micro(c.Required), Micro(c.Observed), c.Met);
        }).ToList();
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(PackActivationFolder.Statement(decision))).ToLowerInvariant();
        var aggregate = new PackAggregate(pack.Id, pack.Version, pack.Digest, pack.Manifest.Trial?.Measure ?? "test-impact", with.Tasks,
            from ?? decision.DecidedAt, to ?? decision.DecidedAt, "none", null, digest, checks, checks.All(c => c.Met));
        using var signer = PackSigner.FromPem(signingKeyPem);
        return PackAggregate.Create(aggregate, signer);
    }

    private static long Micro(double value) => checked((long)Math.Round(value * 1_000_000));
}
