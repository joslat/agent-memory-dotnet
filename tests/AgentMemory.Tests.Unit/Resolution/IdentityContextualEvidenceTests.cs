using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityContextualEvidenceTests
{
    private static IdentityDecisionWrite Input()
    {
        var input = IdentityDecisionPayloadTests.Input();
        return input with { ExpectedCandidates = [new() { Entity = input.Decision.Observation with { EntityId = "target" } }],
            Decision = input.Decision with { Status = IdentityDecisionKind.Pending, PendingEntityIds = ["target"],
                ContextualEvidence = new() { Verdict = IdentityAdjudicationVerdict.Same, CandidateId = "target",
                    Citations = [new() { SourceId = "message", Quote = "is arriving." }] } } };
    }

    [Fact]
    public void Capture_freezes_typed_quotes_and_roundtrips_the_original_contextual_verdict()
    {
        var quotes = new List<IdentityAdjudicationCitation> { new() { SourceId = "message", Quote = "is arriving." } };
        var input = Input(); input = input with { Decision = input.Decision with
        { ContextualEvidence = input.Decision.ContextualEvidence! with { Citations = quotes } } };
        var frozen = IdentityDecisionPayload.Capture(input, "pablito"); quotes.Clear();
        frozen.Decision.ContextualEvidence!.Citations.Should().ContainSingle().Which.Quote.Should().Be("is arriving.");
        IdentityDecisionPayload.Decode(IdentityDecisionPayload.Encode(frozen.Decision)).ContextualEvidence
            .Should().BeEquivalentTo(frozen.Decision.ContextualEvidence);
    }

    [Theory]
    [InlineData("verdict")] [InlineData("candidate")] [InlineData("missing-candidate")]
    [InlineData("empty-quotes")] [InlineData("null-quotes")] [InlineData("source")]
    [InlineData("invented-quote")] [InlineData("oversize-quote")] [InlineData("too-many-quotes")]
    [InlineData("null-quote")] [InlineData("conflicting-source")]
    public void Malformed_typed_receipts_cannot_cross_the_durable_boundary(string change)
    {
        var input = Input(); var evidence = input.Decision.ContextualEvidence!;
        evidence = change switch
        {
            "verdict" => evidence with { Verdict = (IdentityAdjudicationVerdict)99 },
            "candidate" => evidence with { CandidateId = "foreign" },
            "missing-candidate" => evidence with { CandidateId = null },
            "empty-quotes" => evidence with { Citations = [] },
            "null-quotes" => evidence with { Citations = null! },
            "source" => evidence with { Citations = [new() { SourceId = "foreign", Quote = "is arriving." }] },
            "invented-quote" => evidence with { Citations = [new() { SourceId = "message", Quote = "not in the source" }] },
            "oversize-quote" => evidence with { Citations = [new() { SourceId = "message", Quote = new string('x', 2049) }] },
            "too-many-quotes" => evidence with { Citations = Enumerable.Repeat(evidence.Citations[0], 33).ToArray() },
            "null-quote" => evidence with { Citations = [null!] },
            _ => evidence
        };
        input = input with { Decision = input.Decision with { ContextualEvidence = evidence } };
        if (change == "conflicting-source") input = input with { Decision = input.Decision with { Context = input.Decision.Context with
        { Sources = [.. input.Decision.Context.Sources, new() { SourceId = "message", Text = "different text" }] } } };
        var captured = input;
        Action action = () => IdentityDecisionPayload.Capture(captured, "pablito");
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Uncertain_receipts_can_keep_no_candidate_and_no_quotes_without_creating_authority()
    {
        var input = Input(); input = input with { Decision = input.Decision with { ContextualEvidence = new()
        { Verdict = IdentityAdjudicationVerdict.Uncertain } } };
        IdentityDecisionPayload.Capture(input, "pablito").Decision.Status.Should().Be(IdentityDecisionKind.Pending);
    }

    [Theory]
    [InlineData(false, IdentityAdjudicationVerdict.Same)]
    [InlineData(true, IdentityAdjudicationVerdict.Different)]
    [InlineData(true, IdentityAdjudicationVerdict.Uncertain)]
    public void Confirmed_contextual_proposals_require_same_verdict_and_explicit_type_permission(bool allow, IdentityAdjudicationVerdict verdict)
    {
        var input = Input(); input = input with { Decision = input.Decision with
        { Status = IdentityDecisionKind.Confirmed, ConfirmedEntityId = "target", PendingEntityIds = [],
            Policy = new() { AllowContextualConfirmation = allow },
            ContextualEvidence = input.Decision.ContextualEvidence! with { Verdict = verdict } } };
        Action action = () => IdentityDecisionPayload.Capture(input, "pablito");
        action.Should().Throw<ArgumentException>();
    }
}
