using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>Final disposition of a unique candidate group.</summary>
[Experimental("AMEXT001")]
public enum ModuleCandidateDisposition
{
    /// <summary>Selected and rendered.</summary>
    Kept,
    /// <summary>Same record identity had conflicting content or logical keys.</summary>
    DuplicateConflict,
    /// <summary>The host admission policy refused the text.</summary>
    Denied,
    /// <summary>A complete valid judge decision excluded it.</summary>
    JudgeRejected,
    /// <summary>Selected output item allowance exhausted.</summary>
    ItemLimit,
    /// <summary>Raw candidate text exceeded the pre-admission allowance.</summary>
    CandidateByteLimit,
    /// <summary>Candidate-text allowance for the judge batch was exhausted.</summary>
    JudgeByteLimit,
    /// <summary>The complete rendered item would exceed the output allowance.</summary>
    OutputByteLimit,
    /// <summary>Admission or rendering failed.</summary>
    Unavailable
}

/// <summary>Whether a module judge ran, or why bounded admitted items were retained without its decision.</summary>
[Experimental("AMEXT001")]
public enum ModuleJudgeOutcome
{
    /// <summary>Explicitly off; no gate resolution or call.</summary>
    Disabled,
    /// <summary>No admitted candidate remained.</summary>
    NoCandidates,
    /// <summary>Complete finite decision applied, including valid all-negative decisions.</summary>
    Applied,
    /// <summary>Missing/extra keys, null or invalid scores; bounded admitted fallback.</summary>
    InvalidDecision,
    /// <summary>No configured gate or its call failed; bounded admitted fallback.</summary>
    Unavailable
}

/// <summary>A unique candidate and all original contributions coalesced into it.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleCandidateDecision
{
    /// <summary>The canonical candidate used for admission, judging and rendering.</summary>
    public required ModuleContextCandidate Candidate { get; init; }
    /// <summary>All original identities and source references, including duplicate contributors.</summary>
    public IReadOnlyList<ModuleContextCandidate> Origins { get; init; } = [];
    /// <summary>Final disposition.</summary>
    public required ModuleCandidateDisposition Disposition { get; init; }
    /// <summary>True only when the host admitted this unique candidate, even if later omitted.</summary>
    public bool Admitted { get; init; }
    /// <summary>True only when this candidate was included in an actual gate invocation, including failed calls.</summary>
    public bool OfferedToJudge { get; init; }
    /// <summary>Only a validated judge score; null for off or fallback.</summary>
    public double? Score { get; init; }
}

/// <summary>Collection counts before candidate grouping and admission.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleContributorSelection
{
    /// <summary>Compiler section's contributor identity.</summary>
    public required string ContributorId { get; init; }
    /// <summary>Original number of section items; null if collection itself failed before counting.</summary>
    public required int? OfferedItems { get; init; }
    /// <summary>Number projected within contributor and shared limits.</summary>
    public required int ProjectedItems { get; init; }
    /// <summary>Number omitted by those collection limits.</summary>
    public required int ItemLimitOmitted { get; init; }
    /// <summary>Projection failure, when present; not an empty successful search.</summary>
    public ContextOmissionReason? Failure { get; init; }
    /// <summary>Stable projection failure code without input values.</summary>
    public string? FailureDetail { get; init; }
}

/// <summary>Bounded module selection result; no core context is owned or rewritten here.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleSelectionResult
{
    /// <summary>Exact rendered module addition, including renderer-supplied separators.</summary>
    public string RenderedText { get; init; } = string.Empty;
    /// <summary>Exact UTF-8 byte count, not a token estimate.</summary>
    public int RenderedUtf8Bytes { get; init; }
    /// <summary>Judge behavior for this request.</summary>
    public required ModuleJudgeOutcome JudgeOutcome { get; init; }
    /// <summary>Provider-reported judge identity after a valid decision; not a trust claim.</summary>
    public string? AnsweredBy { get; init; }
    /// <summary>Unique candidates with retained source references and final reasons.</summary>
    public IReadOnlyList<ModuleCandidateDecision> Decisions { get; init; } = [];
    /// <summary>Per-contributor collection counts.</summary>
    public IReadOnlyList<ModuleContributorSelection> Contributors { get; init; } = [];
    /// <summary>Existing compiler omissions, preserved separately from selection outcomes.</summary>
    public IReadOnlyList<ContextOmission> CompilationOmissions { get; init; } = [];
}
