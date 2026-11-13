using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityConfigurationTests
{
    [Fact]
    public void Disabled_configuration_is_inert_even_when_unused_values_are_invalid()
    {
        var options = new IdentityResolutionOptions { Enabled = false, MaxCandidates = -1 };
        IdentityConfiguration.Capture(options).Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void Candidate_reads_must_have_a_bounded_supported_limit(int limit)
    {
        var options = new IdentityResolutionOptions { Enabled = true, MaxCandidates = limit };
        var capture = () => IdentityConfiguration.Capture(options);
        capture.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Similarity_thresholds_are_finite_values_in_their_defined_range(double threshold)
    {
        var options = new IdentityResolutionOptions { Enabled = true };
        options.Policies["PERSON"] = new EntityIdentityPolicy { FuzzyReviewThreshold = threshold };
        var capture = () => IdentityConfiguration.Capture(options);
        capture.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void A_policy_cannot_have_duplicate_key_components()
    {
        var options = new IdentityResolutionOptions { Enabled = true };
        options.Policies["PERSON"] = new EntityIdentityPolicy { IdentityProperties = ["employee_id", "employee_id"] };
        var capture = () => IdentityConfiguration.Capture(options);
        capture.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Configuration_is_frozen_before_async_resolution_can_observe_caller_mutation()
    {
        var properties = new List<string> { "employee_id" };
        var options = new IdentityResolutionOptions { Enabled = true, MaxCandidates = 12 };
        options.Policies["PERSON"] = new EntityIdentityPolicy { IdentityProperties = properties };
        var captured = IdentityConfiguration.Capture(options);
        properties.Add("company_id");
        options.Policies.Clear();
        options.MaxCandidates = 1;
        captured.MaxCandidates.Should().Be(12);
        captured.PolicyFor("person").IdentityProperties.Should().Equal("employee_id");
    }

    [Fact]
    public void An_unknown_type_has_conservative_defaults()
    {
        var captured = IdentityConfiguration.Capture(new IdentityResolutionOptions { Enabled = true });
        captured.PolicyFor("NEW_TYPE").AllowContextualConfirmation.Should().BeFalse();
        captured.PolicyFor("NEW_TYPE").IdentityProperties.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    public void Contextual_timeouts_are_positive_and_practically_bounded(int seconds)
    {
        var capture = () => IdentityConfiguration.Capture(new IdentityResolutionOptions
        { Enabled = true, AdjudicationTimeout = TimeSpan.FromSeconds(seconds) });
        capture.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Zero_model_calls_and_zero_source_bytes_are_valid_explicit_budgets()
    {
        var options = new IdentityResolutionOptions { Enabled = true, MaxAdjudicationsPerIngestion = 0, MaxContextUtf8Bytes = 0 };
        var captured = IdentityConfiguration.Capture(options);
        captured.MaxAdjudicationsPerIngestion.Should().Be(0);
        captured.MaxContextUtf8Bytes.Should().Be(0);
    }

    [Fact]
    public void Conversational_policies_open_contextual_confirmation_for_events_people_and_organisations_only()
    {
        var options = new IdentityResolutionOptions();
        options.Policies["PERSON"] = new() { IdentityProperties = ["employeeId"] };
        options.Policies["LOCATION"] = new() { IdentityProperties = ["placeId"] };
        options.ApplyConversationalPolicies().Should().BeSameAs(options);
        options.Enabled.Should().BeFalse("the policy set never enables identity by itself");
        foreach (var type in new[] { "EVENT", "PERSON", "ORGANIZATION" })
            options.Policies[type].Should().Match<EntityIdentityPolicy>(p => p.AllowContextualConfirmation && p.ReviewContextRootedCandidates);
        options.Policies["PERSON"].IdentityProperties.Should().Equal("employeeId");
        options.Policies["LOCATION"].AllowContextualConfirmation.Should().BeFalse();
        options.Policies.Keys.Should().BeEquivalentTo(["EVENT", "PERSON", "ORGANIZATION", "LOCATION"]);
    }
}
