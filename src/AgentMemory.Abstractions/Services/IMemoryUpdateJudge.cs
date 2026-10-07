using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Decides whether a newly written memory replaces a stored one: the judge on the write path.
/// </summary>
/// <remarks>
/// <para>
/// The library closes a memory on its own when a new fact's relation is single-valued and its subject and predicate match
/// an old one's, or when the extractor marks a correction. People mostly say a change in other words ("I'm not doing the
/// 10k anymore" against "is running the Lyon 10k"), and measured on labelled statements that path closed none of 21
/// replaced memories. A judge asked, for each new fact or preference, about its most similar stored memories closed 10 of
/// them, every one rightly, at a probability of at least 0.8.
/// </para>
/// <para>
/// Used only when registered and <see cref="IsEnabled"/>; without one, the write path is exactly what it was. A failing
/// judge closes nothing: a stale memory is a smaller harm than a true one erased.
/// </para>
/// </remarks>
[Experimental("AMGATE001")]
public interface IMemoryUpdateJudge
{
    /// <summary>Whether the write path should ask this judge at all.</summary>
    bool IsEnabled { get; }

    /// <summary>The probability, for every pair, that the new memory replaces the stored one.</summary>
    Task<IReadOnlyDictionary<string, double>> JudgeAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One new memory beside one stored memory it might replace.</summary>
/// <param name="Key">Unique within the request; the answer is keyed by it.</param>
/// <param name="NewMemory">The memory just written, as text.</param>
/// <param name="StoredMemory">The stored memory, as text.</param>
[Experimental("AMGATE001")]
public sealed record MemoryUpdatePair(string Key, string NewMemory, string StoredMemory);

/// <summary>What the update judge decides about.</summary>
/// <param name="Said">What the person said that produced the new memories, when known.</param>
/// <param name="Now">The moment of the write.</param>
/// <param name="Pairs">Every new memory beside each stored memory it might replace.</param>
[Experimental("AMGATE001")]
public sealed record MemoryUpdateRequest(string? Said, DateTimeOffset Now, IReadOnlyList<MemoryUpdatePair> Pairs)
{
    /// <summary>
    /// True when every pair is a closing a store-aware writer named (<see cref="IMemoryWriter"/>, AMWRITE001) rather than a
    /// stored memory found by similarity. The judge is then asked what counts as a replacement: a changed value, a correction,
    /// a more precise statement of the same thing, a plan or activity reported done or called off. Storage round 4 selected
    /// that question on a fresh, unseen world (with the writer: 87.8% storage accuracy against 84.3% with the question below).
    /// False (the default): the question the 1.9 update judge was measured with, for pairs found by similarity.
    /// </summary>
    public bool Named { get; init; }
}
