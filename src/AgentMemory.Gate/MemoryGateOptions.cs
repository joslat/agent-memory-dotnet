using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Gate;

/// <summary>How a recall fills the prompt once the gate is added.</summary>
[Experimental("AMGATE001")]
public enum MemoryGateMode
{
    /// <summary>
    /// Today's recall, as without the gate: every memory type searched, cut by the similarity floor and a cap per type.
    /// Also what <see cref="Judge"/> falls back to when no judge answers in time.
    /// </summary>
    Today = 0,

    /// <summary>
    /// Every memory type searched wide; the judges score every memory found; what reaches
    /// <see cref="MemoryGateOptions.Threshold"/> goes in. The mode the measurements selected.
    /// </summary>
    Judge = 1,

    /// <summary>
    /// Every memory found, no cut and no judge: the fan-out delivered whole. For comparison; it uses ten to twenty
    /// times the tokens of <see cref="Judge"/> and, measured, replies less well.
    /// </summary>
    Everything = 2,
}

/// <summary>One decision-model endpoint speaking the System One protocol (TypeSafe's JEV online, or a local server).</summary>
[Experimental("AMGATE001")]
public sealed class SystemOneEndpoint
{
    /// <summary>A name for diagnostics ("jev", "laya").</summary>
    public string Name { get; set; } = "jev";

    /// <summary>The endpoint, e.g. <c>https://api.typesafe.ai/v1/systemone</c> or <c>http://127.0.0.1:8765/v1/systemone</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The environment variable holding the key (never logged); none for a local server.</summary>
    public string? KeyVariable { get; set; }

    /// <summary>The model the endpoint is asked for.</summary>
    public string Model { get; set; } = "jev-latest";

    /// <summary>
    /// This judge's share of the blended score. Measured: JEV 0.8 and Laya 0.2. Weights are normalised over the judges
    /// that answered, so one judge down leaves the other's score, not a smaller one.
    /// </summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>The gate's settings (<see cref="GateServiceCollectionExtensions.AddAgentMemoryGate"/>).</summary>
[Experimental("AMGATE001")]
public sealed class MemoryGateOptions
{
    /// <summary>How recall fills the prompt. <see cref="MemoryGateMode.Judge"/> once the gate is added.</summary>
    public MemoryGateMode Mode { get; set; } = MemoryGateMode.Judge;

    /// <summary>The judges, blended by <see cref="SystemOneEndpoint.Weight"/>.</summary>
    public IList<SystemOneEndpoint> Judges { get; set; } = [];

    /// <summary>
    /// A memory goes in when its blended probability reaches this. 0.23 was chosen by cross-validation for JEV, alone or
    /// with Laya, at a clutter budget of 6% of the unneeded memories found.
    /// </summary>
    public double Threshold { get; set; } = 0.23;

    /// <summary>How many memories each type is asked for in the wide search (with no similarity floor).</summary>
    public int WideLimit { get; set; } = 50;

    /// <summary>How long the judges may take; past it, recall falls back to <see cref="MemoryGateMode.Today"/>.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A file of labelled turns (JSON: <c>[{ "turn", "types": [...], "memories": [...] }]</c>); the judge is shown the
    /// three most similar as examples of what a reply needed. Optional; without it the judge sees no examples.
    /// </summary>
    public string? ExamplesPath { get; set; }

    /// <summary>How many examples the judge is shown when <see cref="ExamplesPath"/> is set.</summary>
    public int Examples { get; set; } = 3;

    /// <summary>
    /// Whether the write path asks the first judge whether each new fact or preference replaces a stored one
    /// (<c>ExtractionOptions.UpdateJudgeThreshold</c>, 0.8). Off: today's write path.
    /// </summary>
    public bool UpdateJudge { get; set; }
}
