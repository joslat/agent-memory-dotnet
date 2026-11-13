namespace AgentMemory.Abstractions.Services;

/// <summary>A bounded contextual request constructed by the identity policy layer.</summary>
public sealed record IdentityAdjudicationRequest
{
    /// <summary>Policy instructions; source documents are never inserted here.</summary>
    public required string SystemInstructions { get; init; }
    /// <summary>Untrusted mention, candidate and source data encoded as JSON.</summary>
    public required string DataJson { get; init; }
    /// <summary>Requested maximum output tokens for this single call.</summary>
    public required int MaxOutputTokens { get; init; }
}

/// <summary>
/// Optional contextual model seam. The returned JSON is untrusted and must be checked by Core;
/// this service cannot confirm identity, persist memory or override a deterministic veto.
/// </summary>
public interface IIdentityAdjudicator
{
    /// <summary>Make one model call; retries require a separate explicit budget decision.</summary>
    Task<string?> GetDecisionAsync(IdentityAdjudicationRequest request, CancellationToken cancellationToken = default);
}
