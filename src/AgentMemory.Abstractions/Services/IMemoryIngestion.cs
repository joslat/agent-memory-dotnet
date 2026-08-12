using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Write-side memory role: adding messages to short-term memory and extracting/persisting long-term
/// memory from them. Depend on this when a component only ingests memory.
/// </summary>
public interface IMemoryIngestion
{
    /// <summary>
    /// Adds a message to short-term memory.
    /// </summary>
    Task<Message> AddMessageAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message to short-term memory with a caller-supplied, deterministic id. Calling this twice
    /// with the same <paramref name="messageId"/> returns the pre-existing message unchanged (first-write-
    /// wins) instead of creating a duplicate node -- lets independently-configured persisting components
    /// (e.g. multiple MAF integration components observing the same underlying model response) converge on
    /// one message node when they share a stable identity for it, instead of each minting a fresh one.
    /// </summary>
    /// <remarks>
    /// Default implementation ignores <paramref name="messageId"/> and behaves exactly like
    /// <see cref="AddMessageAsync(string,string,string,string,IReadOnlyDictionary{string,object}?,CancellationToken)"/>
    /// (a fresh id every call) -- implementers of this interface are not required to override this member.
    /// </remarks>
    Task<Message> AddMessageWithIdAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        string messageId,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default) =>
        AddMessageAsync(sessionId, conversationId, role, content, metadata, cancellationToken);

    /// <summary>
    /// Batch adds messages to short-term memory.
    /// </summary>
    Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts and persists long-term memory from messages. The returned <see cref="ExtractionResult"/>
    /// carries structured per-item outcomes and an overall <see cref="IngestionStatus"/> (#101). Under the
    /// default <c>ExtractionOptions.FailureMode</c> (<c>IngestionFailureMode.BestEffort</c>) this never
    /// throws for a per-item failure; under <c>IngestionFailureMode.FailFast</c> it throws
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryIngestionException"/> at the first one,
    /// carrying every outcome completed before the failure.
    /// </summary>
    Task<ExtractionResult> ExtractAndPersistAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactively runs the extraction pipeline on all messages in a session and persists the
    /// resulting entities, facts, preferences, and relationships. When <paramref name="userId"/> is
    /// supplied (R1) the extracted nodes are owner-stamped and resolution is owner-scoped; null ⇒ the
    /// nodes are stored as shared/global (the prior single-tenant behavior). Under
    /// <c>IngestionFailureMode.FailFast</c> (#101) can throw
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryIngestionException"/>; this method's <c>void</c>
    /// return means the per-item outcomes are only observable via that exception, not on success.
    /// </summary>
    Task ExtractFromSessionAsync(
        string sessionId,
        string? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactively runs the extraction pipeline on all messages in a conversation and persists the
    /// results. When <paramref name="userId"/> is supplied (R1) the extracted nodes are owner-stamped
    /// and resolution is owner-scoped; null ⇒ stored as shared/global. Under
    /// <c>IngestionFailureMode.FailFast</c> (#101) can throw
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryIngestionException"/>; this method's <c>void</c>
    /// return means the per-item outcomes are only observable via that exception, not on success.
    /// </summary>
    Task ExtractFromConversationAsync(
        string conversationId,
        string? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ingests many extraction requests with bounded concurrency (rank 27).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The documented bulk path. Loading a backlog was always <i>possible</i> — call
    /// <see cref="ExtractAndPersistAsync"/> in a loop, or in a <c>Parallel.ForEachAsync</c> — and both
    /// obvious ways are wrong in opposite directions: the serial loop wastes hours, and the unbounded
    /// parallel one saturates the provider quota and the connection pool, degrading p99 for every
    /// other tenant in the process while median latency looks fine.
    /// </para>
    /// <para>
    /// <b>Composed, not new machinery.</b> This paces calls the host could have made itself; it does
    /// not add a second ingestion path. That matters because a separate bulk pipeline would be a
    /// second place for trust stamping, provenance and owner scoping to drift out of agreement with
    /// the per-request one.
    /// </para>
    /// <para>
    /// A default implementation is supplied so the interface stays SemVer-compatible: an existing
    /// <see cref="IMemoryIngestion"/> implementation keeps compiling and gets a correct, if
    /// unoptimised, bulk path for free.
    /// </para>
    /// </remarks>
    async Task<BulkIngestionResult> IngestBulkAsync(
        IReadOnlyList<ExtractionRequest> requests,
        BulkIngestionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        options ??= new BulkIngestionOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrency);

        var outcomes = new BulkIngestionOutcome?[requests.Count];
        using var gate = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        // Cancels the remaining work when ContinueOnError is false. Linked rather than replacing the
        // caller's token, so a stop-on-error run still honours an outer cancellation.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var running = new List<Task>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            if (stop.IsCancellationRequested) break;

            var index = i;
            running.Add(Task.Run(async () =>
            {
                try
                {
                    await gate.WaitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Queued behind the gate when the run stopped. Left unattempted -- and caught
                    // HERE rather than by the block below, whose finally would Release a slot this
                    // task never acquired.
                    return;
                }

                try
                {
                    var result = await ExtractAndPersistAsync(requests[index], stop.Token)
                        .ConfigureAwait(false);
                    outcomes[index] = new BulkIngestionOutcome(index, result, null);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Left null: never attempted, or abandoned mid-flight. Recorded as
                    // NotAttemptedCount rather than as a failure, because re-running a cancelled
                    // request is correct and re-running a failed one may not be.
                }
                catch (Exception ex)
                {
                    outcomes[index] = new BulkIngestionOutcome(index, null, ex);
                    if (!options.ContinueOnError) await stop.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(running).ConfigureAwait(false);

        // An outer cancellation is still an error for the caller; a stop-on-first-failure is not,
        // because the failure it stopped for is already reported in the outcomes.
        cancellationToken.ThrowIfCancellationRequested();

        var completed = outcomes.Where(o => o is not null).Select(o => o!).ToList();
        return new BulkIngestionResult
        {
            Outcomes = completed,
            NotAttemptedCount = requests.Count - completed.Count,
        };
    }
}
