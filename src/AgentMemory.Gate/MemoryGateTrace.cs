using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Gate;

/// <summary>
/// What the retrieval router did for one recall, attached to the recalled context's metadata under
/// <see cref="MetadataKey"/> whatever the engine: every memory it considered, whether it went in, and the judges'
/// probability when they were asked. For a host that shows or audits recall (a dashboard, a test); spans carry only the
/// counts (content is never a span attribute).
/// </summary>
/// <param name="Mode">The engine configured (<see cref="MemoryGateMode"/>).</param>
/// <param name="Outcome">What actually ran: <c>floor</c>, <c>judge</c>, <c>everything</c>, or <c>floor (fallback)</c>.</param>
/// <param name="FallbackReason">Why the judge did not decide, when <see cref="Outcome"/> is a fallback (or the judge mode
/// has no judge configured); otherwise null.</param>
/// <param name="Threshold">The probability a memory needed to go in (judge only; 0 otherwise).</param>
/// <param name="Offered">How many memories the wide search found (judge, everything, fallback) or the floor kept.</param>
/// <param name="Kept">How many reached the prompt.</param>
/// <param name="AnsweredBy">The judges that answered (judge only), e.g. <c>jev+laya</c>.</param>
/// <param name="JudgeMilliseconds">The judges' time (judge or fallback); 0 otherwise.</param>
/// <param name="Items">Every memory considered, in the order the recall found them.</param>
[Experimental("AMGATE001")]
public sealed record MemoryGateTrace(
    MemoryGateMode Mode,
    string Outcome,
    string? FallbackReason,
    double Threshold,
    int Offered,
    int Kept,
    string AnsweredBy,
    long JudgeMilliseconds,
    IReadOnlyList<MemoryGateTraceItem> Items)
{
    /// <summary>The metadata key the trace is stored under on the recalled context.</summary>
    public const string MetadataKey = "gate.trace";
}

/// <summary>One memory the retrieval router considered.</summary>
/// <param name="ItemId">The memory's id (fact, entity, relationship, preference, message or trace id).</param>
/// <param name="MemoryType">Its type as the judge was told it: semantic, prospective, entity-graph, preference, episodic,
/// reasoning.</param>
/// <param name="Text">The memory as the judge was shown it.</param>
/// <param name="Probability">The blended probability that it helps the reply, when the judges were asked and answered.</param>
/// <param name="Kept">Whether it reached the prompt.</param>
[Experimental("AMGATE001")]
public sealed record MemoryGateTraceItem(string ItemId, string MemoryType, string Text, double? Probability, bool Kept);

/// <summary>The gate's span and its attributes (counts and names only; never a memory's text).</summary>
[Experimental("AMGATE001")]
public static class MemoryGateTelemetry
{
    /// <summary>One live recall through the gate, on the <c>AgentMemory</c> source.</summary>
    public const string Span = "memory.gate";

    /// <summary>The engine configured: floor, judge or everything.</summary>
    public const string Mode = "memory.gate.mode";

    /// <summary>What ran (<see cref="MemoryGateTrace.Outcome"/>).</summary>
    public const string Outcome = "memory.gate.outcome";

    /// <summary>How many memories were considered.</summary>
    public const string Offered = "memory.gate.offered";

    /// <summary>How many reached the prompt.</summary>
    public const string Kept = "memory.gate.kept";

    /// <summary>The judges that answered.</summary>
    public const string JudgedBy = "memory.gate.judged_by";

    /// <summary>The judges' time in milliseconds.</summary>
    public const string JudgeMilliseconds = "memory.gate.judge_ms";

    /// <summary>Why recall fell back to the floor: <c>timeout</c>, <c>no-judge</c>, or the failure's exception type.</summary>
    public const string Fallback = "memory.gate.fallback";
}
