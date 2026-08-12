using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The AgentEval 0.20 sampling surface, which is what makes a per-memory-type claim possible.
/// </summary>
/// <remarks>
/// <para>
/// Sampling was stratified across all six question types with only a count and a seed, so a
/// 50-question subset yielded roughly <b>six</b> <c>single-session-assistant</c> questions — and six
/// cannot carry a per-type claim. Type-filtered sampling is the difference between "30 episodic
/// questions" and "50 questions of which 6 are".
/// </para>
/// <para>
/// Abstention matters for the same reason in reverse: <c>_abs</c> questions are the dataset's only
/// meta-memory signal, and across 52 recorded runs of ours <b>not one ever ran</b>.
/// </para>
/// </remarks>
public sealed class TypedSamplingOptionsTests
{
    private static ExternalBenchmarkOptions Create(
        IReadOnlyList<string>? types = null,
        AbstentionSamplingPolicy abstention = AbstentionSamplingPolicy.AsSampled,
        double? proportion = null,
        bool suppressBoundaries = false) =>
        LongMemEvalBenchmarkProtocol.CreateOptions(
            "dataset.json", questions: 30, seed: 42, judgeRetryAttempts: 2,
            LongMemEvalEvidenceDetail.Identifiers, maxRelevantMessages: 5,
            JudgeVerdictProtocol.FreeText, types, abstention, proportion, suppressBoundaries);

    [Fact]
    public void NoTypeFilterReproducesTheStratifiedDefault()
    {
        // Null, not an empty list: the loader treats null as "no composition filter", and an empty list
        // must not accidentally mean "select nothing".
        Create().IncludeQuestionTypes.Should().BeNull();
        Create(types: Array.Empty<string>()).IncludeQuestionTypes.Should().BeNull();
    }

    [Fact]
    public void ARequestedTypeReachesTheSampler()
    {
        var options = Create(types: new[] { "single-session-assistant" });

        options.IncludeQuestionTypes.Should().ContainSingle()
            .Which.Should().Be("single-session-assistant");
    }

    [Fact]
    public void AbstentionDefaultsToTheHistoricalBehaviour()
    {
        // AsSampled is what every recorded run did. Changing this default would silently alter what a
        // sealed base contains, which is exactly the comparability break to avoid.
        Create().AbstentionPolicy.Should().Be(AbstentionSamplingPolicy.AsSampled);
        Create().AbstentionTargetProportion.Should().BeNull();
    }

    [Theory]
    [InlineData(AbstentionSamplingPolicy.Exclude)]
    [InlineData(AbstentionSamplingPolicy.Only)]
    [InlineData(AbstentionSamplingPolicy.TargetProportion)]
    public void EveryAbstentionPolicyIsReachable(AbstentionSamplingPolicy policy)
    {
        Create(abstention: policy, proportion: 0.25).AbstentionPolicy.Should().Be(policy);
    }

    [Fact]
    public void ProvenanceIsCapturedInFull()
    {
        // Dataset SHA-256, judge-prompt fingerprint, AgentEval version and a fingerprint of the selected
        // question ids. Sealed-base comparability used to be checked by hand-diffing AgentEval's source
        // between releases; this makes it mechanical.
        Create().RunProvenanceMode.Should().Be(RunProvenanceMode.Full);
    }

    [Fact]
    public void SyntheticBoundariesAreKeptByDefaultAndSuppressibleOnRequest()
    {
        // Keeping them by default is what preserves comparability with every sealed base. Being able to
        // omit them matters because for two failing questions, 30 of 30 retrieved messages were this
        // boilerplate -- and it read as a defect in OUR retrieval for weeks.
        Create().SyntheticTurnMarker.Should().BeNull("null means AgentEval's historical default text");
        Create(suppressBoundaries: true).SyntheticTurnMarker.Should().BeEmpty(
            "an empty marker omits the synthetic turn entirely");
    }
}
