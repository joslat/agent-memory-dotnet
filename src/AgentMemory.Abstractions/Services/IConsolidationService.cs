using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Batch memory-hygiene operations (consolidation): archiving expired conversations, removing
/// duplicate preferences, and surfacing duplicate-entity / long-trace candidates. Runs are
/// <b>dry-run by default</b> (report what would change without mutating) and each applied run records
/// a <c>:ConsolidationRun</c> audit node. Mirrors the upstream consolidation primitives (PR #113).
/// Two dreaming operations, off by default (<c>AMDREAM001</c>), close clutter the write path left:
/// <see cref="ConsolidationOptions.CloseGenericEntities"/> and <see cref="ConsolidationOptions.CloseUnsaidPreferences"/>.
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

    /// <summary>
    /// Close generic entities and their connections (dreaming, round 2's P4s): an entity of at most four words that is
    /// an object, or a thing without a name (lower-case, not a person), such as "school backpack" or "new phone", when a
    /// live fact or preference names it, so the fact carries what was said and the entity is clutter. An entity no live
    /// fact or preference names is spared, with its connections: it may be the only place the memory is held. Off by
    /// default. Measured over stores written by the extractors (Dreaming rounds 1-2): with
    /// <see cref="CloseUnsaidPreferences"/>, precision about 54% to 60% with nothing lost; over stores the store-aware
    /// writer wrote it finds almost nothing to close.
    /// </summary>
    [Experimental("AMDREAM001")]
    public bool CloseGenericEntities { get; init; }

    /// <summary>
    /// Close preferences the message they came from never said (dreaming, round 1's P3q): each live preference is shown
    /// to the host's chat model with its source message and the two before it in the conversation; one read into what
    /// was said (a trait, a liking, a generalisation from one remark, someone else's preference) is closed, one partly
    /// said is kept. Needs an <see cref="IPreferenceSourceCheck"/> (registered by the LLM extraction package); one chat
    /// call per source message. Off by default.
    /// </summary>
    [Experimental("AMDREAM001")]
    public bool CloseUnsaidPreferences { get; init; }

    /// <summary>The owner whose memories the dreaming operations read and close; null for every owner.</summary>
    [Experimental("AMDREAM001")]
    public string? OwnerId { get; init; }

    /// <summary>
    /// An apply run that closes only these proposals, by <see cref="ConsolidationProposal.Id"/>, as a dry run reported
    /// them: the application reviews a dry run's proposals and applies the ones it approves. Preferences in the list are
    /// closed without asking the chat model again. Null closes everything the enabled operations find.
    /// </summary>
    [Experimental("AMDREAM001")]
    public IReadOnlyCollection<string>? ApprovedProposals { get; init; }
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

    /// <summary>Generic entities closed (dry run: that would be closed); <see cref="ConsolidationOptions.CloseGenericEntities"/>.</summary>
    [Experimental("AMDREAM001")]
    public int GenericEntitiesClosed { get; init; }

    /// <summary>Connections to those entities closed (dry run: that would be closed).</summary>
    [Experimental("AMDREAM001")]
    public int GenericConnectionsClosed { get; init; }

    /// <summary>Preferences closed as not said (dry run: that would be closed); <see cref="ConsolidationOptions.CloseUnsaidPreferences"/>.</summary>
    [Experimental("AMDREAM001")]
    public int UnsaidPreferencesClosed { get; init; }

    /// <summary>
    /// What the dreaming operations close (dry run: propose), one by one, with why: what an application reviews before
    /// it applies them through <see cref="ConsolidationOptions.ApprovedProposals"/>.
    /// </summary>
    [Experimental("AMDREAM001")]
    public IReadOnlyList<ConsolidationProposal> Proposals { get; init; } = [];

    /// <summary>Total nodes and connections that were (or would be) mutated.</summary>
#pragma warning disable AMDREAM001
    public int TotalChanges => ConversationsArchived + DuplicatePreferencesRemoved
        + GenericEntitiesClosed + GenericConnectionsClosed + UnsaidPreferencesClosed;
#pragma warning restore AMDREAM001
}

/// <summary>One memory a dreaming operation closes, or proposes to close in a dry run.</summary>
/// <param name="Id">The entity's, connection's or preference's id: what <see cref="ConsolidationOptions.ApprovedProposals"/> takes.</param>
/// <param name="Kind"><c>entity</c>, <c>connection</c> or <c>preference</c>.</param>
/// <param name="OwnerId">The owner it belongs to.</param>
/// <param name="Text">The memory as stored: <c>School backpack (OBJECT)</c>, <c>Vasco -[OWNS]-&gt; School backpack</c>, <c>loves spicy food (food)</c>.</param>
/// <param name="Reason">Why it is closed.</param>
[Experimental("AMDREAM001")]
public sealed record ConsolidationProposal(string Id, string Kind, string? OwnerId, string Text, string Reason);
