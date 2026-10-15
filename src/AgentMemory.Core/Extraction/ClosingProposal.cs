using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Extraction;

/// <summary>A typed closing selected by an existing source, before repository application.</summary>
internal abstract record ClosingProposal
{
    public required string TargetId { get; init; }
    public required ClosingSource Source { get; init; }
    public MemoryScope? Scope { get; init; }

    internal sealed record FactClosing : ClosingProposal
    {
        public required string ReplacementId { get; init; }
        public required FactClosureReason Reason { get; init; }
        public DateTimeOffset? ChangedAtUtc { get; init; }
    }

    internal sealed record PreferenceClosing : ClosingProposal
    {
        public required string ReplacementId { get; init; }
    }

    internal sealed record RelationshipEnding : ClosingProposal
    {
        public required DateTimeOffset EndedAtUtc { get; init; }
        // Optional attribution only: EndAsync has no successor argument. Mirrored endings need none.
        public string? ReplacementId { get; init; }
    }

    // Withdrawal does not imply a successor. The repository owns its invalidation clock/history.
    internal sealed record FactWithdrawal : ClosingProposal;
    internal sealed record EntityRetirement : ClosingProposal;

    public static FactClosing Fact(string targetId, string replacementId, FactClosureReason reason,
        DateTimeOffset? changedAtUtc, MemoryScope? scope, ClosingSource source) => new()
    {
        TargetId = targetId, ReplacementId = replacementId, Reason = reason,
        ChangedAtUtc = changedAtUtc, Scope = scope, Source = source,
    };

    public static PreferenceClosing Preference(string targetId, string replacementId,
        MemoryScope? scope, ClosingSource source) => new()
    {
        TargetId = targetId, ReplacementId = replacementId, Scope = scope, Source = source,
    };

    public static RelationshipEnding Relationship(string targetId, DateTimeOffset endedAtUtc,
        MemoryScope? scope, ClosingSource source, string? replacementId = null) => new()
    {
        TargetId = targetId, EndedAtUtc = endedAtUtc, Scope = scope, Source = source, ReplacementId = replacementId,
    };

    public static FactWithdrawal WithdrawFact(string targetId, MemoryScope? scope, ClosingSource source) => new()
    {
        TargetId = targetId, Scope = scope, Source = source,
    };

    public static EntityRetirement RetireEntity(string targetId, MemoryScope? scope, ClosingSource source) => new()
    {
        TargetId = targetId, Scope = scope, Source = source,
    };
}

internal enum ClosingSource
{
    DeterministicReplacement,
    ExtractorCorrection,
    WithinExtractionReplacement,
    SimilarityJudge,
    WriterNamed,
    MirroredEdge,
    WithdrawnEventDate,
    CorrectedName,
}
