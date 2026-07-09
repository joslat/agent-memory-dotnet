namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Long-term memory node families that can be surfaced in history/audit views.
/// </summary>
public enum MemoryHistoryKind
{
    /// <summary>An entity memory node.</summary>
    Entity,

    /// <summary>A factual subject-predicate-object memory node.</summary>
    Fact,

    /// <summary>A preference memory node.</summary>
    Preference,
}

/// <summary>
/// Coarse lifecycle state for a long-term memory node.
/// </summary>
public enum MemoryHistoryStatus
{
    /// <summary>The memory is eligible for live recall.</summary>
    Live,

    /// <summary>The memory was soft-invalidated and is retained for history/as-of recall.</summary>
    Invalidated,
}

/// <summary>
/// Query options for reading long-term memory history. A null <see cref="OwnerId"/> is an unscoped
/// administrative read; a set owner returns that owner's rows plus shared rows when
/// <see cref="IncludeShared"/> is true.
/// </summary>
public sealed record MemoryHistoryQuery
{
    /// <summary>Optional node family filter.</summary>
    public MemoryHistoryKind? Kind { get; init; }

    /// <summary>Optional concrete node id filter.</summary>
    public string? Id { get; init; }

    /// <summary>Optional owner/user filter. Null means unscoped/global read.</summary>
    public string? OwnerId { get; init; }

    /// <summary>When <see cref="OwnerId"/> is set, also include shared/global records.</summary>
    public bool IncludeShared { get; init; } = true;

    /// <summary>Include soft-invalidated/tombstoned records as well as live records.</summary>
    public bool IncludeInvalidated { get; init; } = true;

    /// <summary>Maximum number of records to return.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>
/// A normalized history row for a long-term memory node, preserving lifecycle timestamps,
/// supersession links, provenance message ids, owner scope, read-audit fields, and persisted metadata.
/// </summary>
public sealed record MemoryHistoryRecord
{
    /// <summary>Long-term memory node family.</summary>
    public required MemoryHistoryKind Kind { get; init; }

    /// <summary>The node's stable <c>id</c> property.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable compact description of the memory.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Owner/user id for private memories; null means shared/global.</summary>
    public string? OwnerId { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public required MemoryHistoryStatus Status { get; init; }

    /// <summary>UTC timestamp when the memory was created.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>UTC timestamp when the memory was last updated, when the node carries one.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>UTC timestamp when the memory was soft-invalidated, when applicable.</summary>
    public DateTimeOffset? InvalidatedAtUtc { get; init; }

    /// <summary>UTC timestamp when the memory was last recalled, when tracked.</summary>
    public DateTimeOffset? LastAccessedAtUtc { get; init; }

    /// <summary>Number of tracked recall/access hits for this memory.</summary>
    public int AccessCount { get; init; }

    /// <summary>Number of read-audit rows recorded for this memory.</summary>
    public int ReadAuditCount { get; init; }

    /// <summary>UTC timestamp of the most recent read-audit row, when present.</summary>
    public DateTimeOffset? LastReadAuditAtUtc { get; init; }

    /// <summary>Start of the fact's valid-time window, when applicable.</summary>
    public DateTimeOffset? ValidFromUtc { get; init; }

    /// <summary>End of the fact's valid-time window, when applicable.</summary>
    public DateTimeOffset? ValidUntilUtc { get; init; }

    /// <summary>Ids of memories that supersede this memory.</summary>
    public IReadOnlyList<string> SupersededByIds { get; init; } = Array.Empty<string>();

    /// <summary>Ids of memories superseded by this memory.</summary>
    public IReadOnlyList<string> SupersedesIds { get; init; } = Array.Empty<string>();

    /// <summary>Source message ids captured as provenance.</summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>Persisted node metadata.</summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
}
