namespace AgentMemory.Abstractions.Options;

/// <summary>
/// The arithmetic the session accountant is allowed to perform.
/// </summary>
/// <remarks>
/// <para>
/// Every operator here is <b>deterministic</b> — numeric parse plus graph aggregation, no model in the
/// loop. That is the whole bet: answer-time decomposition died 0/29 on perfect context, and the answer
/// model is the noisiest component in the stack, so arithmetic moves from a stochastic reader to a
/// deterministic writer. An LLM-assisted operator would reintroduce exactly the hallucination surface
/// this exists to remove.
/// </para>
/// <para>
/// <see cref="Duration"/> and <see cref="Sum"/> are <b>not</b> in the default set, for different
/// reasons. Duration needs real dates and the current corpus stamps <c>UnixEpoch + counter</c>, so any
/// duration computed there is fiction. Sum needs to know a predicate is additive — adding two
/// temperatures is nonsense — so it runs only over an explicit allowlist.
/// </para>
/// </remarks>
[Flags]
public enum DerivationOperators
{
    /// <summary>No arithmetic.</summary>
    None = 0,

    /// <summary>How many live facts share this subject and predicate.</summary>
    Count = 1,

    /// <summary>The change between the first and last numeric value in a chain.</summary>
    Delta = 2,

    /// <summary>The most recent value in a chain, by valid time then transaction time.</summary>
    Latest = 4,

    /// <summary>The total of a chain's numeric values. Allowlisted predicates only.</summary>
    Sum = 8,

    /// <summary>Elapsed time between successive dated values of one predicate chain.</summary>
    Duration = 16,

    /// <summary>The distinct objects accumulated under one subject and predicate.</summary>
    SetEnumeration = 32,
}
