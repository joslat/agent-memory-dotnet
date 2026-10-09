using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Gate;

/// <summary>How a recall fills the prompt once the gate is added.</summary>
[Experimental("AMGATE001")]
public enum MemoryGateMode
{
    /// <summary>
    /// Recall as without the gate: every memory type searched, cut by the similarity floor
    /// (<c>RecallOptions.MinSimilarityScore</c>, 0.7) and a cap per type. Also what <see cref="Judge"/> falls back to when
    /// no judge answers in time.
    /// </summary>
    Floor = 0,

    /// <summary>
    /// Every memory type searched wide; the judges score every memory found; what reaches
    /// <see cref="MemoryGateOptions.Threshold"/> goes in. The mode the measurements selected.
    /// </summary>
    Judge = 1,

    /// <summary>
    /// Every memory found, no cut and no judge: the fan-out delivered whole. For a host that values accuracy over tokens:
    /// measured, it carried ten to seventeen times the memory tokens of <see cref="Judge"/>, and replies from it were
    /// graded about five points more accurate.
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

/// <summary>
/// The gate's settings (<see cref="GateServiceCollectionExtensions.AddAgentMemoryGate(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{MemoryGateOptions})"/>,
/// or from the configuration section <see cref="GateServiceCollectionExtensions.SectionName"/>).
/// </summary>
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

    /// <summary>How long the judges may take; past it, recall falls back to <see cref="MemoryGateMode.Floor"/>.</summary>
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
    /// (<c>ExtractionOptions.UpdateJudgeThreshold</c>, 0.65), and, with the store-aware writer (AMWRITE001), whether each
    /// closing it names is one (<c>ExtractionOptions.NamedClosingThreshold</c>, 0.60). Off: the write path without a judge,
    /// and the writer's closings are not applied unless the host's chat model judges them
    /// (<c>LlmExtractionOptions.ChatModelUpdateJudge</c>, which also steps in when this judge fails).
    /// </summary>
    public bool UpdateJudge { get; set; }

    /// <summary>
    /// A judge that failed this many calls in a row (an error, no connection, or the HTTP client's own timeout) is left out
    /// for <see cref="JudgeCooldown"/>, then asked once: an answer brings it back, another failure leaves it out for another
    /// cool-down. Meanwhile recall goes on without it (the floor when no judge is left) and the update judge closes nothing;
    /// the log, the span and the gate's trace say so. 3 by default; 0 asks every judge on every call, as before. Not counted:
    /// a recall cut by <see cref="Timeout"/>, which already caps what a slow judge costs.
    /// </summary>
    public int JudgeFailuresBeforeCooldown { get; set; } = 3;

    /// <summary>How long a judge that kept failing is left out before it is asked again (<see cref="JudgeFailuresBeforeCooldown"/>).</summary>
    public TimeSpan JudgeCooldown { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The gate's half of the Recommended preset (<c>MemoryOptions.CreateRecommended()</c>): the judge fills the prompt
    /// (<see cref="MemoryGateMode.Judge"/>) and the write path asks it about replacements and the writer's closings
    /// (<see cref="UpdateJudge"/>). The judges themselves are the host's to add (<see cref="Judges"/>, or the configuration
    /// section): with none, recall falls back to <see cref="MemoryGateMode.Floor"/>, and the writer's closings are confirmed
    /// by the host's chat model when the extraction half of the preset is on (<c>LlmExtractionOptions.ApplyRecommended()</c>).
    /// Returns this instance.
    /// </summary>
    public MemoryGateOptions ApplyRecommended()
    {
        Mode = MemoryGateMode.Judge;
        UpdateJudge = true;
        return this;
    }
}
