using System.Security.Cryptography;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Resolution;

/// <summary>Review control data is bounded and separate from caller-supplied identity evidence.</summary>
internal static class IdentityReviewPayload
{
    internal static IdentityReviewRequest Capture(IdentityReviewRequest request,string? ownerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        Text(request.DecisionId); Text(request.OperationId); Text(request.Actor); Text(request.Reason);
        if(ownerId is not null) Text(ownerId);
        if(!Enum.IsDefined(request.Action) || request.ExpectedVersion is < 1 or long.MaxValue)
            throw new ArgumentException("A review needs a supported action and an incrementable expected version.");
        if(request.Action==IdentityReviewAction.Revoke)
        {
            if(request.CandidateEntityId is not null) throw new ArgumentException("Revocation selects the current confirmation, not a supplied candidate.");
        }
        else Text(request.CandidateEntityId);
        return request with { };
    }

    internal static string Fingerprint(IdentityReviewRequest request,string? ownerId,bool includeShared) =>
        Convert.ToHexString(SHA256.HashData(IdentityDecisionPayload.Serialize(new { ownerId,includeShared,request })));

    internal static string RevisionId(IdentityReviewRequest request,string? ownerId) =>
        RevisionId(request.DecisionId,request.OperationId,ownerId);

    /// <summary>A review revision's id; one derivation for review and for a restore into the same owner (REV3-8).</summary>
    internal static string RevisionId(string decisionId,string operationId,string? ownerId) =>
        decisionId+"/review/"+Convert.ToHexString(SHA256.HashData(
            IdentityDecisionPayload.Serialize(new { ownerId,OperationId=operationId })));

    private static void Text(string? value)
    { if(string.IsNullOrWhiteSpace(value)||value.Length>4096) throw new ArgumentException("Review text needs 1–4096 characters."); }
}
