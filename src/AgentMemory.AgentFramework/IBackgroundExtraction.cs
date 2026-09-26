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
/// owner and store the turn did even when it runs on another thread.
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
}
