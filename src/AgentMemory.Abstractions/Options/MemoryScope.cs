namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Describes whose long-term memory a recall may see — the owner/user tier of the
/// <c>store ⊃ owner ⊃ session</c> scope model. See
/// <c>docs/archive/Memory_Review_and_Implementation_Plan.md</c> (R1).
/// </summary>
/// <remarks>
/// Semantics: when <see cref="OwnerId"/> is null, no owner filter is applied (global recall —
/// the backward-compatible default). When set, recall returns records whose <c>owner_id</c>
/// equals <see cref="OwnerId"/> OR (when <see cref="IncludeShared"/>) records with no owner
/// (<c>owner_id IS NULL</c>, i.e. shared/global knowledge).
/// <para>
/// <b>Scoping convention across the API (note the two distinct null meanings):</b>
/// <list type="bullet">
///   <item><description><b>Owner-or-shared reads</b> take <c>MemoryScope? scope</c>: <c>null ⇒ no
///   filter (returns ALL owners)</c>; a set scope returns the owner's own + (optionally) shared rows.
///   This is the broadest read — pass a scope in multi-tenant deployments to avoid cross-owner reads.</description></item>
///   <item><description><b>Writes / owner-stamps</b> (e.g. <c>ExtractionRequest.UserId</c>,
///   persistence) take <c>string? ownerId</c>: <c>null ⇒ the record is stored as shared/global</c>
///   (<c>owner_id = null</c>).</description></item>
///   <item><description><b>Exact-owner dedup-on-create reads</b> (e.g. <c>FindDuplicateAsync</c>) take
///   <c>string? ownerId</c>: <c>null ⇒ the shared bucket only</c>.</description></item>
/// </list>
/// So a <c>null</c> string-<c>ownerId</c> means "the shared bucket" (writes/dedup), whereas a
/// <c>null</c> <see cref="MemoryScope"/> means "no filter / all owners" (reads).
/// </para>
/// </remarks>
public sealed record MemoryScope
{
    /// <summary>Owner/user id whose records to include. Null = no owner filter (global; the default).</summary>
    public string? OwnerId { get; init; }

    /// <summary>Also include shared records (those with no owner). Defaults to <see langword="true"/>.</summary>
    public bool IncludeShared { get; init; } = true;

    /// <summary>No owner filter — recall sees all records. This reproduces pre-scope behavior.</summary>
    public static MemoryScope Global { get; } = new();

    /// <summary>Recall sees <paramref name="ownerId"/>'s records plus (optionally) shared records.</summary>
    public static MemoryScope For(string ownerId, bool includeShared = true)
        => new() { OwnerId = ownerId, IncludeShared = includeShared };

    /// <summary>True when an owner filter should actually be applied (i.e. <see cref="OwnerId"/> is set).</summary>
    public bool HasOwnerFilter => !string.IsNullOrEmpty(OwnerId);
}
