namespace AgentMemory.Core.Services;

/// <summary>The kinds of memory an owner's recall can take from the shared (owner-less) corpus.</summary>
internal enum SharedKind
{
    Entity,
    Fact,
    Preference,
}

/// <summary>
/// 37.1b. Whether the current store holds any shared (owner-less) memory of a kind. With
/// <c>MemoryOptions.SharedRecallBudget</c> set, an owner's recall searches the shared rows separately; in a store that
/// has none, that search can only come back empty, so it is skipped. Found by the performance gate: every
/// owner-scoped recall searched twice per memory type, shared knowledge or not.
/// </summary>
/// <remarks>
/// An implementation may cache its answer briefly per store; it must never answer "none" for a store that holds
/// shared rows of the kind for longer than that, or recall would hide shared knowledge. Absent (not registered), the
/// shared search always runs, as before.
/// </remarks>
internal interface ISharedCorpusProbe
{
    ValueTask<bool> HasSharedAsync(SharedKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// A shared row of <paramref name="kind"/> is being written in this process: answer "some" from now on, so a book
    /// taught a moment ago is recalled at once rather than after a cached "none" expires. Called before the write
    /// commits, which is safe: a "some" that turns out wrong only costs one empty search.
    /// </summary>
    void Saw(SharedKind kind);
}
