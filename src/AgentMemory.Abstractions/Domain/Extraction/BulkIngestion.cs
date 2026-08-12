namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// How a bulk ingestion run is paced (rank 27).
/// </summary>
/// <remarks>
/// <para>
/// Backpressure is the whole point. A caller with ten thousand conversations to load will otherwise
/// issue them all at once, and the failure is not a clean rejection — it is provider throttling,
/// connection-pool exhaustion and a p99 that degrades for every <i>other</i> tenant sharing the
/// process while median latency looks untouched.
/// </para>
/// </remarks>
public sealed record BulkIngestionOptions
{
    /// <summary>
    /// How many requests may be in flight at once.
    /// </summary>
    /// <remarks>
    /// Defaults to 4: high enough to be worth using over a serial loop, low enough that a caller who
    /// never tunes it does not saturate a shared provider quota by accident. The measured effect of
    /// bounding this is small at the median and <b>20–70% at p99 under saturation</b> — which is to
    /// say it does not make bulk loading faster, it stops bulk loading making everything else slower.
    /// </remarks>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// Whether a failed request stops the run.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>: in a ten-thousand-conversation load, one malformed
    /// transcript aborting the other 9,999 is rarely what the caller wanted. Failures are returned
    /// per request rather than thrown, so continuing is not the same as ignoring.
    /// </remarks>
    public bool ContinueOnError { get; init; } = true;
}

/// <summary>What happened to one request in a bulk run.</summary>
/// <param name="Index">Position in the submitted list, so a failure can be traced to its input.</param>
/// <param name="Result">The extraction result, or <see langword="null"/> if it failed.</param>
/// <param name="Error">The failure, or <see langword="null"/> if it succeeded.</param>
public sealed record BulkIngestionOutcome(int Index, ExtractionResult? Result, Exception? Error)
{
    /// <summary>Whether this request was ingested.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>
/// The result of a bulk ingestion run (rank 27).
/// </summary>
/// <remarks>
/// <b>Per-request outcomes, not an aggregate count.</b> A bulk API that returns "8,412 of 10,000
/// succeeded" tells the caller they have a problem and nothing about which inputs to retry — so the
/// realistic response is to re-run everything, which is worse than the failure. Every outcome carries
/// its index and its exception.
/// </remarks>
public sealed record BulkIngestionResult
{
    /// <summary>One entry per submitted request, in submission order.</summary>
    public required IReadOnlyList<BulkIngestionOutcome> Outcomes { get; init; }

    /// <summary>How many requests were ingested.</summary>
    public int SucceededCount => Outcomes.Count(o => o.Succeeded);

    /// <summary>How many requests failed.</summary>
    public int FailedCount => Outcomes.Count(o => !o.Succeeded);

    /// <summary>
    /// Requests that were never attempted because the run stopped early.
    /// </summary>
    /// <remarks>
    /// Distinct from a failure, and reported separately for the same reason 6.5's trace counts are
    /// nullable: "tried and failed" and "never tried" call for different actions, and collapsing them
    /// makes a stopped run look like a partially broken corpus.
    /// </remarks>
    public int NotAttemptedCount { get; init; }
}
