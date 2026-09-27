namespace AgentMemory.AgentFramework;

/// <summary>
/// Runs a turn's extraction after the turn has returned (<see cref="AgentFrameworkOptions.ExtractInBackground"/>).
/// </summary>
/// <remarks>
/// <para>
/// The default implementation, registered by <c>AddAgentMemoryFramework</c>, runs work in process: in
/// order per ordering key (a session's turns are learned in the order they happened), concurrently
/// across keys up to <see cref="AgentFrameworkOptions.BackgroundExtractionConcurrency"/>, and drains on
/// shutdown for up to <see cref="AgentFrameworkOptions.BackgroundDrainTimeout"/>. Register your own to
/// hand the work to another scheduler.
/// </para>
/// <para>
/// Work is enqueued with the caller's ambient owner and store scope captured, so it writes to the same
/// owner and store the turn did even when it runs on another thread. The owner also travels explicitly;
/// the store travels only as the ambient scope, so a custom <c>IMemoryStoreContext</c> that reads through
/// a per-request holder (an <c>IHttpContextAccessor</c>, say) sees nothing once the request has ended.
/// </para>
/// <para>
/// The default queue resolves the work's services from a fresh scope and drains when the container is
/// disposed: work still queued then needs the database driver, so a host that disposes the driver first
/// (or resolves this queue before any memory service) should drain explicitly with
/// <see cref="WhenIdleAsync"/> when stopping. The queue is not bounded: under a slow model, backlog grows
/// with the turns.
/// </para>
/// </remarks>
public interface IBackgroundExtraction
{
    /// <summary>
    /// Queues <paramref name="work"/>. Returns <see langword="false"/> when the queue no longer accepts
    /// work (it is shutting down); the caller then runs the work itself.
    /// </summary>
    /// <param name="orderingKey">Work with the same key runs one at a time, in the order it was queued.</param>
    /// <param name="work">The work; its token is cancelled only when the queue gives up draining.</param>
    bool TryEnqueue(string orderingKey, Func<CancellationToken, Task> work);

    /// <summary>Work queued or running.</summary>
    int Pending { get; }

    /// <summary>Completes when nothing is queued or running.</summary>
    Task WhenIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes when nothing with this ordering key is queued or running: the next turn of an owner waits
    /// on this, so it can recall what that owner just said. The default waits for everything.
    /// </summary>
    Task WhenIdleAsync(string orderingKey, CancellationToken cancellationToken = default) => WhenIdleAsync(cancellationToken);
}
