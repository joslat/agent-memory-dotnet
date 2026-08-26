namespace AgentMemory.Abstractions.Domain;

/// <summary>What one sub-query actually contributed, measured rather than assumed.</summary>
/// <remarks>
/// The three counts are deliberately distinct. <see cref="ItemsRetrieved"/> says the leg ran;
/// <see cref="UniqueContributions"/> says it found something the monolithic query would have missed;
/// <see cref="SurvivedBudget"/> says that something reached the prompt. A mechanism can score well on
/// the first and zero on the third, and reporting only the first is how fan-out would look valuable
/// while changing nothing.
/// </remarks>
public sealed record SubQueryYield
{
    /// <summary>The memory type this leg targeted.</summary>
    public required MemoryTypeAffinity Affinity { get; init; }

    /// <summary>The text this leg retrieved with.</summary>
    public required string QueryText { get; init; }

    /// <summary>Items this leg returned, raw and pre-merge.</summary>
    public required int ItemsRetrieved { get; init; }

    /// <summary>Of those, the ids absent from the monolithic result sets.</summary>
    public required int UniqueContributions { get; init; }

    /// <summary>Of those unique ids, how many were still present after budget truncation.</summary>
    public required int SurvivedBudget { get; init; }
}

/// <summary>
/// The fan-out witness: whether the planner ran, whether it fired, and what it bought.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three states must stay distinguishable</b>, and that is this type's contract rather than a
/// convenience:
/// </para>
/// <list type="bullet">
/// <item><description><c>MemoryContext.FanOutReport is null</c> — the planner <b>never ran</b>.</description></item>
/// <item><description><see cref="GateFired"/> false — it ran and <b>declined</b>; no retrieval cost was paid.</description></item>
/// <item><description><see cref="GateFired"/> true with every <see cref="SubQueryYield.UniqueContributions"/> zero —
/// it fired and <b>contributed nothing</b>.</description></item>
/// </list>
/// <para>
/// Collapsing the third into the second is precisely how query formulation was retired at exactly
/// 0.0000 without anyone being able to say whether it had run — a null that reads as a measurement.
/// </para>
/// </remarks>
public sealed record RecallFanOutReport
{
    /// <summary>Whether the gate elected to fan out.</summary>
    public required bool GateFired { get; init; }

    /// <summary>Which named rules fired — "C1", "C2", "E3", "D4", "W".</summary>
    public IReadOnlyList<string> FiredRules { get; init; } = [];

    /// <summary>Which deriver produced the sub-queries: "det-v1", "llm-v1", or "caller".</summary>
    public string? DeriverId { get; init; }

    /// <summary>Per-leg yield.</summary>
    public IReadOnlyList<SubQueryYield> SubQueries { get; init; } = [];

    /// <summary>
    /// Null when this report can be read as a measurement; otherwise why it cannot.
    /// </summary>
    /// <remarks>
    /// A run that cannot be interpreted must self-void rather than report a zero it did not earn —
    /// e.g. "derivation-failed", "embedding-failed:2/3", "asof-not-supported".
    /// </remarks>
    public string? VoidReason { get; init; }
}
