using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityReviewPayloadTests
{
    internal static IdentityReviewRequest Input()=>new()
    { DecisionId="decision",OperationId="operation",ExpectedVersion=1,Action=IdentityReviewAction.Confirm,
        CandidateEntityId="target",Actor="human:pablito",Reason="Confirmed the source identity" };

    [Theory]
    [InlineData("decision")] [InlineData("operation")] [InlineData("actor")] [InlineData("reason")]
    [InlineData("version")] [InlineData("overflow")] [InlineData("action")] [InlineData("candidate")]
    [InlineData("revoke-candidate")] [InlineData("oversize")]
    public void Invalid_control_data_fails_before_any_backend_work(string change)
    {
        var request=Input(); request=change switch
        {
            "decision"=>request with { DecisionId=" " }, "operation"=>request with { OperationId=" " },
            "actor"=>request with { Actor=" " }, "reason"=>request with { Reason=" " },
            "version"=>request with { ExpectedVersion=0 }, "overflow"=>request with { ExpectedVersion=long.MaxValue },
            "action"=>request with { Action=(IdentityReviewAction)99 }, "candidate"=>request with { CandidateEntityId=null },
            "revoke-candidate"=>request with { Action=IdentityReviewAction.Revoke },
            _=>request with { Reason=new string('x',4097) }
        };
        Action capture=()=>IdentityReviewPayload.Capture(request,"pablito");
        capture.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Retry_fingerprint_includes_owner_permission_version_action_actor_reason_and_candidate()
    {
        var input=Input(); string Hash(IdentityReviewRequest request,string? owner="pablito",bool shared=false)=>
            IdentityReviewPayload.Fingerprint(IdentityReviewPayload.Capture(request,owner),owner,shared);
        var original=Hash(input); Hash(input with { }).Should().Be(original);
        Hash(input,"other").Should().NotBe(original); Hash(input,shared:true).Should().NotBe(original);
        Hash(input with { ExpectedVersion=2 }).Should().NotBe(original);
        Hash(input with { Action=IdentityReviewAction.Reject }).Should().NotBe(original);
        Hash(input with { Actor="other-reviewer" }).Should().NotBe(original);
        Hash(input with { Reason="Different reason" }).Should().NotBe(original);
        Hash(input with { CandidateEntityId="another" }).Should().NotBe(original);
    }

    [Fact]
    public void The_operation_revision_key_is_stable_even_when_retry_payload_is_invalidly_changed()
    {
        var input=Input();
        IdentityReviewPayload.RevisionId(input,"pablito").Should().Be(IdentityReviewPayload.RevisionId(input with { Reason="changed" },"pablito"));
        IdentityReviewPayload.RevisionId(input,"pablito").Should().NotBe(IdentityReviewPayload.RevisionId(input,"other"));
    }

    [Fact]
    public void Initial_decision_payload_cannot_forge_a_backend_review_receipt()
    {
        var input=IdentityDecisionPayloadTests.Input();
        input=input with { Decision=input.Decision with { LatestReview=new()
        { OperationId="fake",RequestHash="fake",Action=IdentityReviewAction.Confirm,CandidateEntityId="target",
            Actor="claimed-reviewer",Reason="claimed authority",PreviousVersion=0,Version=1,PerformedAtUtc=DateTimeOffset.UnixEpoch } } };
        Action capture=()=>IdentityDecisionPayload.Capture(input,"pablito");
        capture.Should().Throw<ArgumentException>();
    }
}
