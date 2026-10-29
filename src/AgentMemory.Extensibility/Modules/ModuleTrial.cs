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
    public const int MinimumTasks = AgentMemory.Shared.PackNamespace.MinimumTrialTasks;
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
    /// When set (0.9, 0.95, 0.975 or 0.99), a rate target is met only when the one-sided Wilson lower bound of the rate at this
    /// confidence reaches it, so a small trial cannot pass by luck.
    /// </summary>
    public double? Confidence { get; init; }
    /// <summary>The targets; every one given must be met.</summary>
    public required ModuleTrialTargets Targets { get; init; }
    /// <summary>
    /// How an upgrade is gated (PACK-upgrade-gate): with <see cref="ModuleTrialUpgrade.NoRegression"/>, a new version goes on
    /// only if, on the same tasks, it does not do worse than the version that passed on this host before it.
    /// </summary>
    public ModuleTrialUpgrade? Upgrade { get; init; }
}

/// <summary>A trial's upgrade rule (pack format v1.2 §7, claim 6).</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTrialUpgrade
{
    /// <summary>The largest tolerated drop in hit rate or precision against the active version, 0 to 0.5.</summary>
    public const double MaximumTolerance = 0.5;

    /// <summary>
    /// Whether the new version must not do worse than the active one: its hit rate and precision, measured on the same tasks
    /// in the same run, may drop by at most <see cref="Tolerance"/>.
    /// </summary>
    public required bool NoRegression { get; init; }
    /// <summary>How much worse is still no regression, 0 to 0.5; default 0.</summary>
    public double Tolerance { get; init; }
}

/// <summary>A trial's targets. Each is optional; at least one must be given.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTrialTargets
{
    /// <summary>The least share of tasks whose truth the pack offers, 0 to 1.</summary>
    public double? HitRate { get; init; }
    /// <summary>The least share of what the pack offers that is in the truth, 0 to 1.</summary>
    public double? Precision { get; init; }
    /// <summary>The least improvement in hit rate over the baseline, -1 to 1.</summary>
    public double? Lift { get; init; }
    /// <summary>The least improvement in precision over the baseline, -1 to 1.</summary>
    public double? PrecisionLift { get; init; }
    /// <summary>The most p95 latency the pack may add to a compile, in milliseconds.</summary>
    public double? AddedP95Ms { get; init; }
}
