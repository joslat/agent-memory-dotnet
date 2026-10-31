using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// The trial a pack must pass on a host before that host switches it on (pack format v1.1 §4, trial-gated activation). The
/// host draws the tasks from its own history, runs them with and without the pack, and activates the pack only if every
/// target here is met; a pack that helps elsewhere but not here stays off here, with the reason.
/// </summary>
/// <remarks>
/// A host enforces trials only when it is given its activation records (<see cref="ModuleSetBuilder.RequireTrials"/>).
/// Without them every added module composes as before.
/// </remarks>
[Experimental("AMEXT001")]
public sealed record ModuleTrial
{
    /// <summary>The fewest tasks a trial may run, so a target is not met by luck.</summary>
    public const int MinimumTasks = PackIds.PackNamespace.MinimumTrialTasks;
    /// <summary>The most tasks a trial runs.</summary>
    public const int MaximumTasks = 200;

    /// <summary>What the trial measures. <c>test-impact</c>: for a change, are the tests it changed offered from the files it changed.</summary>
    public required string Measure { get; init; }
    /// <summary>How many tasks the host draws from its own history, the most recent first.</summary>
    public int Tasks { get; init; } = 40;
    /// <summary>
    /// What "without the pack" means: <c>host</c>, the retrieval the host already has (for code, a grep for the types the
    /// changed files declare); or <c>none</c>, nothing. Lift is measured against it.
    /// </summary>
    public string Baseline { get; init; } = "none";
    /// <summary>
    /// When set, in thousandths (900, 950, 975 or 990), a target is met only when its one-sided lower confidence bound at this
    /// confidence reaches it (<see cref="PackTrialGate.Statistic"/>), so a small trial cannot pass by luck.
    /// </summary>
    public int? ConfidencePermille { get; init; }
    /// <summary>The targets; every one given must be met.</summary>
    public required ModuleTrialTargets Targets { get; init; }
    /// <summary>
    /// How an upgrade is gated (PACK-upgrade-gate): with <see cref="ModuleTrialUpgrade.NoRegression"/>, a new version goes on
    /// only if, on the same tasks, it does not do worse than the version that passed on this host before it.
    /// </summary>
    public ModuleTrialUpgrade? Upgrade { get; init; }
    /// <summary>
    /// The safety target (feature 04): a probe suite the host runs, injection prompts placed as records, and the most its records
    /// may make the model follow. Absent from the declarations' digest when unset, so a trial without one keeps its digest.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModuleTrialSafety? Safety { get; init; }
}

/// <summary>A trial's safety target (feature 04, R-TRIAL-052).</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTrialSafety
{
    /// <summary>The probe suite the host runs: one line of 1 to 100 display-safe characters.</summary>
    public required string Probes { get; init; }
    /// <summary>The most the model may follow the instructions the probes plant, in millionths of the probes, 0 to 1,000,000.</summary>
    public long MaxFollowMicro { get; init; }
}

/// <summary>What a host's own probes showed (feature 04): the suite it ran, how many probes, and how many the model followed.</summary>
[Experimental("AMEXT001")]
public sealed record PackTrialSafetyRun(string Suite, int Probes, int Followed);

/// <summary>
/// The host's safety probes (feature 04): it places a suite's injection prompts as records of the pack, runs its model, and
/// counts how often the model followed them. The library stays model-free; the host supplies this.
/// </summary>
[Experimental("AMEXT001")]
public interface IPackSafetyProbes
{
    /// <summary>Runs <paramref name="suite"/> against the pack the manifest declares.</summary>
    Task<PackTrialSafetyRun> RunAsync(ModuleManifest manifest, string suite, CancellationToken cancellationToken);
}

/// <summary>A trial's upgrade rule (pack format v1.2 §7, claim 6).</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTrialUpgrade
{
    /// <summary>
    /// Whether the new version must not do worse than the active one: its hit rate and precision, measured on the same tasks
    /// in the same run, may drop by at most <see cref="ToleranceMicro"/>.
    /// </summary>
    public required bool NoRegression { get; init; }
    /// <summary>How much worse is still no regression, in millionths, 0 to 500,000; default 0.</summary>
    public long ToleranceMicro { get; init; }
}

/// <summary>
/// The least a trial must claim before this host counts it (<see cref="ModuleSetBuilder.RequireTrialFloor"/>; review 6, M8: a
/// pack chose its own targets, so <c>{"hitRate":0}</c> without a confidence always passed). A trial below the floor is left
/// off whatever its decisions say.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackTrialFloor
{
    /// <summary>The least confidence a trial declares, in thousandths; a trial without one is below any. Null: estimates are enough.</summary>
    public int? ConfidencePermille { get; init; }
    /// <summary>The fewest tasks a trial runs.</summary>
    public int Tasks { get; init; } = ModuleTrial.MinimumTasks;
    /// <summary>
    /// Each target named here the trial must declare, at least as strict: a rate, precision or lift at least this high, an
    /// added latency at most this.
    /// </summary>
    public ModuleTrialTargets? Targets { get; init; }
}

/// <summary>
/// What a host requires of trials, handed whole to every path that judges one (review 7, G6: the module set passed the floor
/// and the gate check did not, so <c>ampack gate</c> and <c>serve</c> showed and served packs the host keeps off). Built
/// from the builder in one place; a new requirement added here reaches every path.
/// </summary>
[Experimental("AMEXT001")]
internal sealed record PackTrialPolicy(IReadOnlyList<string> Mandatory, IReadOnlyList<string> CertificateTrust, PackTrialFloor? Floor)
{
    /// <summary>Nothing required beyond a passing trial for what declares one.</summary>
    internal static PackTrialPolicy None { get; } = new([], [], null);

    /// <summary>The policy a builder holds.</summary>
    internal static PackTrialPolicy Of(ModuleSetBuilder builder) => new(builder.MandatoryTrials, builder.CertificateTrust, builder.TrialFloor);

    /// <summary>
    /// Why this policy cannot hold, in order; empty when it can. The one check every door that applies the policy runs before
    /// anything else (review 9, B-M1: only the module set ran it, so ampack gate and serve showed and served packs under a policy a
    /// host refuses to start with, a floor that a pack declaring no trial walks past).
    /// </summary>
    /// <param name="decisions">Whether the host's signed decisions are given.</param>
    internal IReadOnlyList<string> Problems(bool decisions)
    {
        var problems = new List<string>();
        if (!decisions && Mandatory.Count != 0)
            problems.Add("RequireTrialsFor (--require-trials-for) makes trials mandatory, so it needs the host's decisions: call RequireTrials too (--activations).");
        if (!decisions && CertificateTrust.Count != 0)
            problems.Add("TrustCertificatesFor (--trust-certificates-for) accepts decisions made from certificates, so it needs the host's decisions: call RequireTrials too (--activations).");
        if (!decisions && Floor is not null)
            problems.Add("RequireTrialFloor (--floor-*) sets what a trial must claim, so it needs the host's decisions: call RequireTrials too (--activations).");
        // A floor a pack could skip by declaring no trial holds nothing (review 7, D7).
        if (Floor is not null && Mandatory.Count == 0)
            problems.Add("RequireTrialFloor (--floor-*) needs RequireTrialsFor (--require-trials-for): otherwise a pack that declares no trial composes past the floor.");
        return problems;
    }
}

/// <summary>A trial's targets. Each is optional; at least one must be given.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTrialTargets
{
    /// <summary>The least share of tasks whose truth the pack offers, in millionths, 0 to 1,000,000.</summary>
    public long? HitRateMicro { get; init; }
    /// <summary>The least share of what the pack offers that is in the truth, in millionths, 0 to 1,000,000.</summary>
    public long? PrecisionMicro { get; init; }
    /// <summary>The least improvement in hit rate over the baseline, in millionths, -1,000,000 to 1,000,000.</summary>
    public long? LiftMicro { get; init; }
    /// <summary>The least improvement in precision over the baseline, in millionths, -1,000,000 to 1,000,000.</summary>
    public long? PrecisionLiftMicro { get; init; }
    /// <summary>The most p95 latency the pack may add to a compile, in whole milliseconds, 1 to 60,000.</summary>
    public long? AddedP95Ms { get; init; }

    /// <summary>
    /// Each target given, as the gate checks it: a rate or a lift as a fraction of one, the added latency in milliseconds.
    /// </summary>
    internal IEnumerable<(string Name, double Value)> Checked() =>
        Given().Select(g => (g.Name, g.Name == "addedP95Ms" ? (double)g.Value : PackNumbers.Fraction(g.Value)));

    /// <summary>Each target given, by the name a certificate's checks use, in the unit of its field.</summary>
    internal IEnumerable<(string Name, long Value)> Given()
    {
        if (HitRateMicro is { } hit) yield return ("hitRate", hit);
        if (PrecisionMicro is { } precision) yield return ("precision", precision);
        if (LiftMicro is { } lift) yield return ("lift", lift);
        if (PrecisionLiftMicro is { } precisionLift) yield return ("precisionLift", precisionLift);
        if (AddedP95Ms is { } added) yield return ("addedP95Ms", added);
    }
}
