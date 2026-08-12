using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The judge options adopted from AgentEval 0.19, and the one that must stay opt-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="JudgeVerdictProtocol.StructuredJson"/> is not the default.</b> It is strictly
/// better at recovering a verdict — AgentEval's own documentation names the free-text failure mode as
/// vetoing a leading "yes" when the word "no" appears later in the response, which is exactly the
/// systematic failure this project saw on question <c>7405e8b1</c>. But the same documentation states
/// that enabling it "changes verdicts on the questions the free-text parser mis-scored" and therefore
/// "makes results non-comparable with a base recorded under the free-text protocol".
/// </para>
/// <para>
/// Every sealed base here was recorded under free text. A default flip would silently invalidate all
/// of them, so the protocol is a stated per-run decision.
/// </para>
/// </remarks>
public sealed class JudgeProtocolOptionsTests
{
    private static ExternalBenchmarkOptions Create(
        JudgeVerdictProtocol? protocol = null) =>
        protocol is null
            ? LongMemEvalBenchmarkProtocol.CreateOptions(
                "dataset.json", questions: 10, seed: 42, judgeRetryAttempts: 2,
                LongMemEvalEvidenceDetail.Identifiers, maxRelevantMessages: 5)
            : LongMemEvalBenchmarkProtocol.CreateOptions(
                "dataset.json", questions: 10, seed: 42, judgeRetryAttempts: 2,
                LongMemEvalEvidenceDetail.Identifiers, maxRelevantMessages: 5, protocol.Value);

    [Fact]
    public void TheDefaultProtocolIsFreeText_SoSealedBasesStayComparable()
    {
        Create().JudgeVerdictProtocol.Should().Be(JudgeVerdictProtocol.FreeText);
    }

    [Fact]
    public void StructuredJsonIsReachableWhenAskedForExplicitly()
    {
        // It must be available -- it fixes a real, systematic mis-scoring -- but only on request.
        Create(JudgeVerdictProtocol.StructuredJson)
            .JudgeVerdictProtocol.Should().Be(JudgeVerdictProtocol.StructuredJson);
    }

    [Fact]
    public void TheRawJudgeResponseIsRetainedIndependentlyOfEvidenceMode()
    {
        // Retention and rendering are different questions. Coupling them is how a later move to a
        // shorter evidence mode would silently remove the only signal that separates a WRONG judge
        // from an UNPARSEABLE one.
        var options = Create();

        options.RetainRawJudgeResponse.Should().BeTrue();
        options.JudgeEvidenceMode.Should().Be(JudgeEvidenceMode.Raw);
    }

    [Fact]
    public void EverythingElseAboutTheProtocolIsUnchanged()
    {
        // The 0.19 adoption must move exactly the two fields above and nothing else, or a sealed base
        // becomes incomparable for a reason nobody wrote down.
        var options = Create();

        options.StratifiedSampling.Should().BeTrue();
        options.RandomSeed.Should().Be(42);
        options.MaxQuestions.Should().Be(10);
        options.DatasetMode.Should().Be("S");
        options.PreserveSessionBoundaries.Should().BeTrue();
        options.IncludeTimestamps.Should().BeTrue();
        options.JudgeFailurePolicy.Should().Be(JudgeFailurePolicy.RetryThenInconclusive);
        options.JudgeTemperature.Should().BeNull();
        options.JudgeMaxOutputTokens.Should().Be(256);
        options.HistoryInjectionMode.Should().Be(HistoryInjectionMode.StructuredChatHistory);
    }

    [Fact]
    public void DecompositionStaysOffByDefault()
    {
        // Per-predicate judging multiplies judge calls per question. It is worth having, but its cost
        // must be a decision rather than something inherited from a package upgrade.
        Create().JudgeDecompositionMode.Should().Be(JudgeDecompositionMode.None);
    }
}
