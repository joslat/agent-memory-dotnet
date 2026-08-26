namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Controls per-memory-type recall fan-out (Proposal M, 30.10).
/// </summary>
/// <remarks>
/// <para>
/// A mutable class, never an init-only record. That is the #100 lesson stated as code: sub-options
/// reached through <c>MemoryOptions</c> must be assignable from a <c>configureMemory</c> lambda, and
/// an init-only record cannot be, so the option silently cannot be configured by the one path hosts
/// actually use.
/// </para>
/// <para>
/// Every default here is the off-state. With <see cref="Enabled"/> false and no caller-supplied
/// sub-queries the planner never runs, no service call is made, and the assembled context is
/// byte-identical to the pre-feature path — which is asserted by a guard test rather than asserted
/// in prose.
/// </para>
/// </remarks>
public sealed class RecallFanOutOptions
{
    /// <summary>Off by default. When false the planner never runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Use the LLM deriver instead of the deterministic one. Off by default.
    /// </summary>
    /// <remarks>
    /// The deterministic deriver is the default because it is reproducible: a model deriving the
    /// sub-queries makes every downstream number depend on a sampled output, and a mechanism whose
    /// measurement cannot be repeated cannot be shown to work.
    /// </remarks>
    public bool UseLlmDerivation { get; set; }

    /// <summary>Hard cap on legs per recall. Also caps what the LLM deriver may return.</summary>
    /// <remarks>
    /// A cap rather than a budget: each leg costs an embedding and a retrieval round trip, so an
    /// uncapped deriver turns one recall into an unbounded fan of them.
    /// </remarks>
    public int MaxSubQueries { get; set; } = 4;

    /// <summary>
    /// Fire signal W when the best monolithic score falls below this. Null disables W.
    /// </summary>
    /// <remarks>
    /// Null rather than a baked-in constant, deliberately. 0.7 is a <i>measured dead zone</i> on one
    /// corpus, not a property of retrieval, and shipping it as a default would bake one corpus's
    /// calibration into everyone's recall.
    /// </remarks>
    public double? WeakTopScoreThreshold { get; set; }

    /// <summary>Distinct capitalised entity mentions required for signal E3.</summary>
    public int MinDistinctEntityMentions { get; set; } = 3;
}
