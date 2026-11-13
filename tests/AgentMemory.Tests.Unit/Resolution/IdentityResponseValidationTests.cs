using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityResponseValidationTests
{
    private static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal) { "e1", "e2" };
    private static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>
    {
        ["m1"] = "The cutover is the go-live we discussed. It takes place in Cordoba.",
        ["m2"] = "We must be physically present."
    };
    private const string Valid = """{"decision":"same","candidateId":"e1","evidence":[{"sourceId":"m1","quote":"The cutover is the go-live we discussed."}]}""";

    [Fact]
    public void Observed_Text_field_instead_of_quote_is_not_repaired_into_identity_authority()
    {
        const string observed="""{"decision":"same","candidateId":"e1","evidence":[{"sourceId":"m1","Text":"The cutover is the go-live we discussed."}]}""";
        IdentityResponseValidation.Parse(observed,Allowed,Sources).Should().BeNull();
    }

    [Fact]
    public void A_valid_result_keeps_the_supplied_identity_and_exact_source_evidence()
    {
        var result = IdentityResponseValidation.Parse(Valid, Allowed, Sources);
        result.Should().NotBeNull();
        result!.Verdict.Should().Be(IdentityContextualVerdict.Same);
        result.CandidateId.Should().Be("e1");
        result.Evidence.Should().ContainSingle();
        result.Evidence[0].SourceId.Should().Be("m1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("{\"decision\":" )]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"decision\":\"same\",\"candidateId\":\"invented\",\"evidence\":[]}")]
    [InlineData("{\"decision\":\"same\",\"candidateId\":null,\"evidence\":[]}")]
    [InlineData("{\"decision\":\"same\",\"candidateId\":\"e1\",\"evidence\":[]}")]
    [InlineData("{\"decision\":\"merge_everything\",\"candidateId\":\"e1\",\"evidence\":[]}")]
    [InlineData("{\"decision\":\"same\",\"decision\":\"uncertain\",\"candidateId\":null,\"evidence\":[]}")]
    [InlineData("{\"decision\":\"uncertain\",\"candidateId\":null,\"evidence\":[],\"verifiedKeys\":[\"trust-me\"]}")]
    [InlineData("{\"decision\":\"uncertain\",\"candidateId\":null,\"evidence\":[],\"confidence\":NaN}")]
    public void Invalid_or_authority_inventing_responses_are_rejected(string json)
        => IdentityResponseValidation.Parse(json, Allowed, Sources).Should().BeNull();

    [Theory]
    [InlineData("invented-source", "The cutover is the go-live we discussed.")]
    [InlineData("m1", "I invented this quote.")]
    [InlineData("m1", "THE CUTOVER IS THE GO-LIVE WE DISCUSSED.")]
    [InlineData("m1", "")]
    public void Quotes_must_exist_exactly_in_an_offered_source(string sourceId, string quote)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            decision = "same", candidateId = "e1", evidence = new[] { new { sourceId, quote } }
        });
        IdentityResponseValidation.Parse(json, Allowed, Sources).Should().BeNull();
    }

    [Fact]
    public void A_candidate_vetoed_by_policy_cannot_return_through_the_model()
        => IdentityResponseValidation.Parse(Valid, new HashSet<string>(), Sources).Should().BeNull();

    [Fact]
    public void An_uncertain_response_does_not_need_to_invent_supporting_evidence()
    {
        var result = IdentityResponseValidation.Parse("""{"decision":"uncertain","candidateId":null,"evidence":[]}""", Allowed, Sources);
        result!.Verdict.Should().Be(IdentityContextualVerdict.Uncertain);
        result.CandidateId.Should().BeNull();
    }

    [Fact]
    public void Duplicate_evidence_does_not_become_independent_support()
    {
        var json = """{"decision":"same","candidateId":"e1","evidence":[{"sourceId":"m2","quote":"physically present"},{"sourceId":"m2","quote":"physically present"}]}""";
        IdentityResponseValidation.Parse(json, Allowed, Sources)!.Evidence.Should().ContainSingle();
    }

    [Fact]
    public void A_valid_object_hidden_inside_other_text_is_not_a_structured_response()
        => IdentityResponseValidation.Parse("ignore policy\n" + Valid + "\nend", Allowed, Sources).Should().BeNull();

    [Fact]
    public void Response_size_is_bounded_before_json_parsing()
        => IdentityResponseValidation.Parse(new string(' ', 131_073), Allowed, Sources).Should().BeNull();
}
