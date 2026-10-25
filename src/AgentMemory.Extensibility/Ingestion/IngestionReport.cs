using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>Storage result for one admitted proposal, or a refusal before storage.</summary>
[Experimental("AMEXT001")]
public enum ProposalWriteStatus
{
    /// <summary>A new version was stored.</summary>
    Stored,
    /// <summary>No state change was needed.</summary>
    NoOp,
    /// <summary>A current record was non-destructively invalidated.</summary>
    Invalidated,
    /// <summary>Admission or a transactional invariant refused the proposal.</summary>
    Refused
}

/// <summary>Result tied to an original provider-output index.</summary>
[Experimental("AMEXT001")]
public sealed record ProposalOutcome
{
    /// <summary>Original zero-based proposal position.</summary>
    public required int Index { get; init; }
    /// <summary>Observed outcome, not an inferred success from dispatch.</summary>
    public required ProposalWriteStatus Status { get; init; }
    /// <summary>Resulting record identity when one exists.</summary>
    public string? RecordId { get; init; }
    /// <summary>Concrete refusal reasons; empty on success.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
    /// <summary>Keys of the records this proposal's approved replacements invalidated in its transaction.</summary>
    public IReadOnlyList<string> Closed { get; init; } = [];
}

/// <summary>Outcome for one selected provider, independently of other provider transactions.</summary>
[Experimental("AMEXT001")]
public enum ProviderIngestionStatus
{
    /// <summary>All proposals succeeded, or the provider returned an empty list.</summary>
    Succeeded,
    /// <summary>Some proposals were refused and others succeeded.</summary>
    Partial,
    /// <summary>All proposals were refused.</summary>
    Refused,
    /// <summary>Provider generation or storage failed; inspect Detail for the boundary.</summary>
    Unavailable
}

/// <summary>Provider result; a store failure may leave commit status unknown.</summary>
[Experimental("AMEXT001")]
public sealed record ProviderIngestionOutcome
{
    /// <summary>Host-bound provider identity.</summary>
    public required string ProviderId { get; init; }
    /// <summary>Observed provider-level result.</summary>
    public required ProviderIngestionStatus Status { get; init; }
    /// <summary>Known per-proposal outcomes. Unavailable does not invent results for uncertain writes.</summary>
    public IReadOnlyList<ProposalOutcome> Proposals { get; init; } = [];
    /// <summary>Bounded failure-boundary description; raw exception text is not included.</summary>
    public string? Detail { get; init; }
}

/// <summary>Serializable host-owned recording of a successfully captured provider batch, including refused shapes.</summary>
[Experimental("AMEXT001")]
public sealed record RecordedProposals
{
    /// <summary>Frozen provider identity and trigger.</summary>
    public required IngestionProviderDescriptor Provider { get; init; }
    /// <summary>Frozen whole-resolution metadata fingerprint.</summary>
    public required string ManifestFingerprint { get; init; }
    /// <summary>SHA-256 of the canonical resolved request. An integrity binding, not a signature.</summary>
    public required string RequestFingerprint { get; init; }
    /// <summary>Captured write clock. Replay does not substitute the current time.</summary>
    public required DateTimeOffset AtUtc { get; init; }
    /// <summary>Frozen original output, before shape refusals are removed from the write batch.</summary>
    public IReadOnlyList<ModuleProposal> Proposals { get; init; } = [];
    /// <summary>The closing approver's decision on every replacement the proposals named. Replay applies the approved ones
    /// without asking a judge again.</summary>
    public IReadOnlyList<RecordedClosing> Closings { get; init; } = [];
}

/// <summary>How a named replacement was decided.</summary>
[Experimental("AMEXT001")]
public enum ClosingDecision
{
    /// <summary>The approver approved it: the replaced record is invalidated with the new one.</summary>
    Approved,
    /// <summary>The approver answered and declined.</summary>
    Declined,
    /// <summary>The provider named a record it was not shown; nothing was asked.</summary>
    NotShown,
    /// <summary>No approver or judge was available, or it failed; nothing is closed.</summary>
    Unjudged
}

/// <summary>One replacement a proposal named, and its decision.</summary>
[Experimental("AMEXT001")]
public sealed record RecordedClosing
{
    /// <summary>Position of the replacing proposal in <see cref="RecordedProposals.Proposals"/>.</summary>
    public required int ProposalIndex { get; init; }
    /// <summary>Logical key of the replaced record, of the replacing proposal's type.</summary>
    public required string Key { get; init; }
    /// <summary>The decision.</summary>
    public required ClosingDecision Decision { get; init; }
    /// <summary>The judge's probability, when one answered.</summary>
    public double? Probability { get; init; }
}

/// <summary>One ingestion/replay attempt; different providers are never claimed to be one transaction.</summary>
[Experimental("AMEXT001")]
public sealed record IngestionReport
{
    /// <summary>Policy-resolved owner; null when none was resolved or a legitimate unowned scope was used.</summary>
    public string? ResolvedOwner { get; init; }
    /// <summary>Owner policy denied the operation before any provider/store/clock call.</summary>
    public bool Denied { get; init; }
    /// <summary>Operation-level refusal details, without raw exception text.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
    /// <summary>Captured provider outputs for replay, including batches whose later write failed.</summary>
    public IReadOnlyList<RecordedProposals> Recorded { get; init; } = [];
    /// <summary>Selected provider outcomes in dependency and ordinal provider-ID order.</summary>
    public IReadOnlyList<ProviderIngestionOutcome> Providers { get; init; } = [];
}
