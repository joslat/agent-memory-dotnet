namespace AgentMemory.Abstractions.Services;

/// <summary>
/// The write path's one approval of closings: whether a new memory replaces a stored one, decided with the same judge
/// selection, host chat-model fallback, thresholds and negative verdicts the core writer uses (Extensibility 0.12, W3).
/// Module ingestion asks it before closing a record a module proposal names; Core implements it over its own policy.
/// </summary>
/// <remarks>
/// A closing is approved only by a valid answer at or above the policy's threshold. No judge, a failing judge, a missing
/// answer or an invalid score approves nothing: a stale memory is a smaller harm than a true one erased.
/// </remarks>
public interface IMemoryClosingApprover
{
    /// <summary>Whether a judge can decide closings of this kind now: <paramref name="named"/> for closings a writer names.</summary>
    bool CanApprove(bool named);

    /// <summary>Decides every pair of the request; the answer is keyed by <see cref="MemoryUpdatePair.Key"/>.</summary>
    Task<IReadOnlyDictionary<string, MemoryClosingVerdict>> ApproveAsync(MemoryUpdateRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>One pair's verdict.</summary>
/// <param name="Probability">The judge's probability that the new memory replaces the stored one; null when none answered.</param>
/// <param name="Approved">Whether the policy approves the closing.</param>
public sealed record MemoryClosingVerdict(double? Probability, bool Approved);
