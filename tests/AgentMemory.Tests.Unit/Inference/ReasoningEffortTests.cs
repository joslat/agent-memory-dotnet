using AgentMemory.Inference;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.Inference;

/// <summary>
/// ILC-32: GLM-5.3-Flash at its default, maximum reasoning can spend its whole reply budget thinking and answer nothing.
/// The identity benchmarks ran it at low effort; hosts built with this package ran it at maximum. Bitdeer's default model
/// now gets low unless the operator says otherwise (AI_REASONING_EFFORT), and no other model gets an effort it did not
/// ask for, because some providers reject the parameter on models that do not reason.
/// </summary>
public sealed class ReasoningEffortTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void Bitdeer_s_default_model_runs_at_low_effort_as_measured()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k")));

        resolution.Settings!.ReasoningEffort.Should().Be(ReasoningEffort.Low);
    }

    [Fact]
    public void The_automatic_effort_names_the_model_it_was_chosen_for_and_an_explicit_one_names_none()
    {
        InferenceProviderEnvironment.Resolve(Env(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k")))
            .Settings!.ReasoningEffortModel.Should().Be("zai-org/GLM-5.3-Flash");
        InferenceProviderEnvironment.Resolve(Env(("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k"), ("AI_REASONING_EFFORT", "low")))
            .Settings!.ReasoningEffortModel.Should().BeNull("an operator's effort applies to every model");
    }

    [Fact]
    public void Another_model_gets_no_effort_it_did_not_ask_for()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k"), ("BITDEER_MODEL", "some-org/a-plain-chat-model")));

        resolution.Settings!.ReasoningEffort.Should().BeNull();
    }

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("Medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("extra-high", ReasoningEffort.ExtraHigh)]
    public void The_operator_sets_the_effort_for_any_provider(string value, ReasoningEffort expected)
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "openai"), ("OPENAI_API_KEY", "k"), ("AI_REASONING_EFFORT", value)));

        resolution.Settings!.ReasoningEffort.Should().Be(expected);
    }

    [Fact]
    public void Default_turns_the_bitdeer_default_off()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k"), ("AI_REASONING_EFFORT", "default")));

        resolution.Settings!.ReasoningEffort.Should().BeNull("'default' sends no effort, so the model's own default applies");
    }

    [Fact]
    public void A_misspelled_effort_fails_closed_and_names_the_choices()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k"), ("AI_REASONING_EFFORT", "lowest")));

        resolution.IsConfigured.Should().BeFalse();
        resolution.Diagnostic.Should().Contain("AI_REASONING_EFFORT").And.Contain("lowest").And.Contain("extra-high");
    }

    [Fact]
    public void The_effort_variable_is_listed_so_tests_scrub_it() =>
        InferenceProviderEnvironment.AllVariables.Should().Contain("AI_REASONING_EFFORT");
}
