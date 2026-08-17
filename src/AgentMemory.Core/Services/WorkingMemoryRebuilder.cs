using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Services;

/// <summary>
/// The one place the working-memory rebuild epilogue and its failure policy live.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because the policy was written out twice — in <c>LongTermMemoryService</c> and in
/// <c>PersistenceStage</c> — and the two copies had already drifted: the identical
/// clear-also-failed branch logged at <c>Error</c> in one ("because nothing else can notice it")
/// and at <c>Warning</c> in the other. A third copy was about to be added for
/// <c>MergeEntitiesAsync</c>. One copy, called from every seam, is the fix.
/// </para>
/// <para>
/// <b>Awaited inline, not fire-and-forget.</b> The contract the staleness canaries test is "after
/// the write call returns, the block is current" — a fire-and-forget rebuild would trade that
/// contract for a few milliseconds on a write that already cost about a second of extraction.
/// </para>
/// <para>
/// <b>Never fails the write.</b> A rebuild is derived bookkeeping; a caller who successfully stored
/// something must not see an exception because a projection of it could not be recompiled. On
/// failure the stored block is CLEARED rather than left stale, because absence degrades to the
/// pre-feature behaviour while staleness manufactures knowledge-update errors.
/// </para>
/// <para>
/// <b>Guard G3 lives at the other end</b> (<c>Neo4jWorkingMemoryService.ShouldSkip</c>): an
/// ownerless write — which is what every TCK bridge write is — must not reach a MERGE on a null
/// identity key. The owner check here is the near-side half of the same guard.
/// </para>
/// </remarks>
internal sealed class WorkingMemoryRebuilder
{
    private readonly IWorkingMemoryService? _workingMemory;
    private readonly WorkingMemoryOptions _options;
    private readonly ILogger _logger;
    private readonly IDisposable? _ownedScope;

    /// <param name="workingMemory">The tier, or null when a host never registered it.</param>
    /// <param name="options">Working-memory options; the enabled gates are read from here.</param>
    /// <param name="logger">Logger for the never-throw failure path.</param>
    /// <param name="ownedScope">
    /// A DI scope this rebuilder owns and disposes after one rebuild. Supplied only by the
    /// repository-decorator seam, which runs at a TRANSIENT lifetime and therefore cannot borrow a
    /// SCOPED working-memory service from the ambient provider. Null everywhere else, where the
    /// caller already lives inside a scope and simply passes the service it was injected with.
    /// </param>
    internal WorkingMemoryRebuilder(
        IWorkingMemoryService? workingMemory,
        WorkingMemoryOptions options,
        ILogger logger,
        IDisposable? ownedScope = null)
    {
        _workingMemory = workingMemory;
        _options = options;
        _logger = logger;
        _ownedScope = ownedScope;
    }

    /// <summary>
    /// True when a rebuild would do nothing, so callers can skip work they would otherwise do only
    /// to feed a rebuild that is switched off.
    /// </summary>
    internal bool IsDisabled =>
        _workingMemory is null || !_options.Enabled || !_options.RebuildOnWrite;

    /// <summary>
    /// Recompiles the owner's block after a write that changed long-term memory. Never throws.
    /// </summary>
    /// <param name="ownerId">Owner whose block to recompile; blank is a no-op (guard G3).</param>
    /// <param name="trigger">
    /// What caused the rebuild, used only in log messages so a failure can be traced to the seam
    /// that triggered it rather than to this shared helper.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task RebuildAsync(
        string? ownerId, string trigger, CancellationToken cancellationToken)
    {
        using var _ = _ownedScope;

        if (IsDisabled) return;
        if (string.IsNullOrWhiteSpace(ownerId)) return;

        try
        {
            await _workingMemory!.RebuildAsync(ownerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Working-memory rebuild failed for owner {Owner} after {Trigger}; the write itself "
                + "succeeded.", ownerId, trigger);

            if (!_options.ClearOnRebuildFailure) return;
            try
            {
                await _workingMemory!.ClearAsync(ownerId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception clearFailure)
            {
                // The residual risk this design accepts and names: if the CLEAR also fails, a stale
                // block can survive. Error, not Warning, because nothing else can notice it — the
                // write returned successfully and the block looks perfectly well-formed.
                _logger.LogError(
                    clearFailure,
                    "Working-memory block for owner {Owner} could not be cleared after a failed "
                    + "rebuild triggered by {Trigger}; it may now be STALE.", ownerId, trigger);
            }
        }
    }
}
