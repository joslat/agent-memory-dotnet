using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>
/// What the compiler produced for one request: the sections it assembled, and what it left out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Omissions are a first-class field, not a log line.</b> A context that silently lacks a section
/// is indistinguishable from one where the section had nothing to say, and those are opposite facts:
/// the first means the answer may be wrong, the second means it is complete. Every contributor that
/// could have run and did not appears in <see cref="Omissions"/> with a reason.
/// </para>
/// <para>
/// This is the same distinction the core library already draws for its own sections
/// (<c>SearchedAndEmpty</c> against <c>AbandonedToLatencyBudget</c>), carried up to the module layer.
/// </para>
/// </remarks>
[Experimental("AMEXT001")]
public sealed record ContextEnvelope(
    ContextRequest Request,
    ContextSnapshot Snapshot,
    IReadOnlyList<ContextSection> Sections,
    IReadOnlyList<ContextOmission> Omissions,
    DateTimeOffset CompiledAt)
{
    /// <summary>An envelope with no sections and no omissions, for a request nothing applied to.</summary>
    public static ContextEnvelope Empty(ContextRequest request, ContextSnapshot snapshot, DateTimeOffset at) =>
        new(request, snapshot, [], [], at);
}

/// <summary>
/// What was true about the world when the envelope was compiled.
/// </summary>
/// <param name="Owner">The owner the request RESOLVED to, never the one it asked for.</param>
/// <param name="AsOf">The valid-time pin, when the request was a point-in-time read.</param>
/// <param name="SystemAsOf">The transaction-time pin.</param>
/// <remarks>
/// The resolved owner is recorded rather than the requested one because resolution is where the
/// isolation policy acts; an envelope that echoed the request would be unable to show that anything
/// had been enforced.
/// </remarks>
[Experimental("AMEXT001")]
public sealed record ContextSnapshot(
    string? Owner,
    DateTimeOffset? AsOf,
    DateTimeOffset? SystemAsOf);

/// <summary>One typed block of context, from one contributor.</summary>
/// <param name="TypeId">Stable section type, e.g. <c>core.rendered</c> or <c>notes.recent</c>.</param>
/// <param name="ContributorId">Which contributor produced it.</param>
/// <param name="SchemaVersion">The section payload's own version, so a renderer can refuse a shape it does not know.</param>
/// <param name="Items">The section's items, ordered as the contributor intends them read.</param>
/// <param name="Metadata">
/// Opaque per-section data a renderer or the host may use. Slice A carries the core
/// <c>MemoryContext</c> here so the MAF renderer can delegate without the compiler parsing it.
/// </param>
[Experimental("AMEXT001")]
public sealed record ContextSection(
    string TypeId,
    string ContributorId,
    int SchemaVersion,
    IReadOnlyList<ContextItem> Items,
    object? Metadata = null);

/// <summary>One item inside a section.</summary>
/// <param name="Id">Stable id, where the contributor has one — used for provenance and de-duplication.</param>
/// <param name="Text">The item as it should be read.</param>
[Experimental("AMEXT001")]
public sealed record ContextItem(string? Id, string Text);

/// <summary>Why a contributor that could have run produced nothing.</summary>
/// <param name="ContributorId">The contributor.</param>
/// <param name="Reason">The reason, from <see cref="ContextOmissionReason"/>.</param>
/// <param name="Detail">Optional human-readable detail, e.g. the budget that was exceeded.</param>
[Experimental("AMEXT001")]
public sealed record ContextOmission(string ContributorId, ContextOmissionReason Reason, string? Detail = null);

/// <summary>Why a contributor was omitted. Distinct reasons, because they mean different things.</summary>
[Experimental("AMEXT001")]
public enum ContextOmissionReason
{
    /// <summary>The request did not match the contributor's applicability. Not a failure.</summary>
    NotApplicable,

    /// <summary>The token or item budget was exhausted before this contributor ran.</summary>
    Budget,

    /// <summary>The contributor exceeded its deadline.</summary>
    Timeout,

    /// <summary>A policy refused it — admission, isolation, or a permission the module lacks.</summary>
    Denied,

    /// <summary>The contributor threw, or its backing store was unreachable.</summary>
    Unavailable,
}
