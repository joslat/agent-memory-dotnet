namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Batch memory-hygiene operations (consolidation): archiving expired conversations, removing
/// duplicate preferences, and surfacing duplicate-entity / long-trace candidates. Runs are
/// <b>dry-run by default</b> (report what would change without mutating) and each applied run records
/// a <c>:ConsolidationRun</c> audit node. Mirrors the upstream consolidation primitives (PR #113).
/// </summary>
public interface IConsolidationService
{
    /// <summary>
    /// Runs the enabled consolidation operations and returns a report. When
    /// <see cref="ConsolidationOptions.DryRun"/> is true (the default), nothing is mutated — the
    /// counts describe what an apply run would do.
    /// </summary>
    Task<ConsolidationReport> ConsolidateAsync(
        ConsolidationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Toggles and thresholds for <see cref="IConsolidationService.ConsolidateAsync"/>.</summary>
public sealed record ConsolidationOptions
{
    /// <summary>When true (default) nothing is mutated; the report describes what would change.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Archive conversations not updated within <see cref="ConversationExpiry"/>.</summary>
    public bool ArchiveExpiredConversations { get; init; } = true;

    /// <summary>Remove exact-duplicate preferences (same owner + category + text), keeping the newest.</summary>
    public bool RemoveDuplicatePreferences { get; init; } = true;

    /// <summary>Count duplicate-entity candidates (same owner + name + type). Detection only.</summary>
    public bool DetectDuplicateEntities { get; init; } = true;

    /// <summary>Count reasoning traces with more than <see cref="LongTraceStepThreshold"/> steps. Detection only.</summary>
    public bool DetectLongTraces { get; init; } = true;

    /// <summary>Age past which a conversation (by <c>updated_at</c>) is considered expired. Default 90 days.</summary>
    public TimeSpan ConversationExpiry { get; init; } = TimeSpan.FromDays(90);

    /// <summary>Step count above which a reasoning trace is a summarization candidate. Default 20.</summary>
    public int LongTraceStepThreshold { get; init; } = 20;
}

/// <summary>Result of a consolidation run.</summary>
public sealed record ConsolidationReport
{
    /// <summary>Unique id of this run (also the <c>:ConsolidationRun</c> node id when applied).</summary>
    public required string RunId { get; init; }

    /// <summary>True if this was a dry run (nothing mutated).</summary>
    public required bool DryRun { get; init; }

    /// <summary>When the run executed.</summary>
    public required DateTimeOffset RanAtUtc { get; init; }

    /// <summary>Conversations archived (dry run: that would be archived).</summary>
    public int ConversationsArchived { get; init; }

    /// <summary>Redundant preferences removed (dry run: that would be removed).</summary>
    public int DuplicatePreferencesRemoved { get; init; }

    /// <summary>Duplicate-entity candidates detected (redundant count beyond one per group).</summary>
    public int DuplicateEntitiesDetected { get; init; }

    /// <summary>Reasoning traces exceeding the step threshold (summarization candidates).</summary>
    public int LongTraceCandidates { get; init; }

    /// <summary>Total nodes that were (or would be) mutated.</summary>
    public int TotalChanges => ConversationsArchived + DuplicatePreferencesRemoved;
}
