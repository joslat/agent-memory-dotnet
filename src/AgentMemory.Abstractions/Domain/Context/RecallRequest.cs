using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Request for recalling memory context.
/// </summary>
public sealed record RecallRequest
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Current user query or message.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional query embedding for semantic search.
    /// </summary>
    public float[]? QueryEmbedding { get; init; }

    /// <summary>
    /// Recall options.
    /// </summary>
    public RecallOptions Options { get; init; } = RecallOptions.Default;

    /// <summary>
    /// The instant a relative time expression in <see cref="Query"/> should be measured from, when
    /// <c>MemoryOptions.ResolveTemporalQueries</c> is enabled. Null (the default) means the clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For a live turn the clock is correct and this should stay null.</b> It exists for the case
    /// where it is not: a host replaying a recorded conversation, backfilling a transcript, or
    /// draining a queue is asking <i>"ten days before the message was sent"</i>, not ten days before
    /// now. Resolving against wall-clock there does not merely fail to help — it binds the query to a
    /// window the corpus cannot contain, so a recall that would have returned the right rows returns
    /// none, and the failure reads as "temporal resolution does not work".
    /// </para>
    /// <para>
    /// Deliberately scoped to <i>parsing the query</i> and nothing else. It is not a general clock
    /// override: access timestamps, decay, and validity all continue to use the real one, because a
    /// replayed conversation is still being read now, and moving those would silently rewrite
    /// retention behaviour to buy a parsing fix.
    /// </para>
    /// </remarks>
    public DateTimeOffset? TemporalReferenceTime { get; init; }

    /// <summary>
    /// Per-memory-type legs to retrieve separately and merge (Proposal M, 30.10). Null is today's
    /// single-query path, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This member exists because the mechanism was otherwise <b>unexpressible</b>. The independent
    /// audit's finding 11 is that the asked-for design — split the query into per-memory-type
    /// sub-queries, retrieve each against the real store, merge the retrieved contexts, answer once —
    /// had a substitute built in its place, and no part of the request schema could even state it.
    /// </para>
    /// <para>
    /// Supplying these directly makes the caller the deriver (reported as <c>"caller"</c>); leaving it
    /// null lets the planner decide, when the feature is enabled.
    /// </para>
    /// </remarks>
    public IReadOnlyList<RecallSubQuery>? SubQueries { get; init; }
}
