using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The reference arms must reject what they do not understand, and draw the questions they are told to.
/// </summary>
/// <remarks>
/// <para>
/// Before this, <c>--reference-arm</c> read the handful of names it knew and ignored the rest. So a
/// command meant to bracket the August reference 50 (<c>--abstention target --abstention-proportion
/// 0.4</c>) ran on the as-sampled 50 instead. The two sets share 4 questions, and the report looked
/// like a normal floor or ceiling.
/// </para>
/// <para>
/// The sampling and judge-protocol flags now come from the same parser the prepared pair uses, so the
/// two verbs draw and judge the same way from the same command line.
/// </para>
/// </remarks>
public sealed class ReferenceArmCommandLineTests : IDisposable
{
    private readonly string _dataset = Path.GetTempFileName();

    public void Dispose() => File.Delete(_dataset);

    private LongMemEvalReferenceArmProgram.ReferenceOptions Parse(params string[] extra) =>
        LongMemEvalReferenceArmProgram.Parse(
            ["--reference-arm", "no-memory", "--dataset", _dataset, "--questions", "50", "--seed", "42", .. extra]);

    [Fact]
    public void AnUnknownFlagIsRejectedByName()
    {
        var act = () => Parse("--abstension", "target");

        act.Should().Throw<ArgumentException>().WithMessage("*'--abstension'*");
    }

    [Fact]
    public void TheThreeSamplingAndJudgeFlagsReachTheBenchmarkOptions()
    {
        var options = Parse(
            "--abstention", "target", "--abstention-proportion", "0.4", "--judge-protocol", "structured-json");

        var benchmark = LongMemEvalReferenceArmProgram.CreateBenchmarkOptions(options);

        benchmark.AbstentionPolicy.Should().Be(AbstentionSamplingPolicy.TargetProportion);
        benchmark.AbstentionTargetProportion.Should().Be(0.4);
        benchmark.JudgeVerdictProtocol.Should().Be(JudgeVerdictProtocol.StructuredJson);
        benchmark.MaxQuestions.Should().Be(50);
        benchmark.RandomSeed.Should().Be(42);
    }

    /// <summary>
    /// Without the flags, nothing changes: every earlier reference arm was drawn and judged this way.
    /// </summary>
    [Fact]
    public void WithoutTheFlagsTheArmDrawsAndJudgesExactlyAsBefore()
    {
        var benchmark = LongMemEvalReferenceArmProgram.CreateBenchmarkOptions(Parse());

        benchmark.AbstentionPolicy.Should().Be(AbstentionSamplingPolicy.AsSampled);
        benchmark.AbstentionTargetProportion.Should().BeNull();
        benchmark.JudgeVerdictProtocol.Should().Be(JudgeVerdictProtocol.FreeText);
    }

    [Theory]
    [InlineData("--memory-mode", "structured")]
    [InlineData("--prepared-pair", "x")]
    public void AMemoryArmFlagKeepsItsSpecificRefusal(string flag, string value)
    {
        var act = () => Parse(flag, value);

        act.Should().Throw<ArgumentException>().WithMessage("*cannot be combined with --reference-arm*");
    }

    /// <summary>
    /// Forwarding structured-json is only half the job: the validator must stop re-parsing prose.
    /// </summary>
    /// <remarks>
    /// Under StructuredJson the judge does not answer in prose, and the memory-arm validator learned in
    /// 3.7 to take AgentEval's Correct as the verdict. Had the reference arm kept its free-text re-parse,
    /// the first structured run would have rejected every question.
    /// </remarks>
    [Fact]
    public void AStructuredVerdictIsAcceptedWithoutAProseReparse()
    {
        QuestionResult[] results = [Structured("q1", true), Structured("q2", false)];
        LongMemEvalReferenceTelemetry[] telemetry =
        [
            new(1, "q1", "completed", 4, 2, 1_000, 250),
            new(2, "q2", "completed", 4, 2, 1_000, 250),
        ];

        var structured = LongMemEvalReferenceArmValidator.Validate(
            2, 4, telemetry, results, verdictProtocol: JudgeVerdictProtocol.StructuredJson);
        var freeText = LongMemEvalReferenceArmValidator.Validate(2, 4, telemetry, results);

        structured.Accepted.Should().BeTrue(string.Join(" | ", structured.Issues));
        structured.CorrectQuestions.Should().Be(1);
        freeText.Accepted.Should().BeFalse("prose re-parsing cannot read a structured verdict");
    }

    private static QuestionResult Structured(string id, bool correct) => new()
    {
        QuestionId = id,
        QuestionType = "single-session-user",
        Question = "What did I study?",
        GoldAnswer = "Business Administration",
        AgentResponse = correct ? "Business Administration" : "I do not know",
        Correct = correct,
        RawScore = correct ? 100 : 0,
        JudgeExplanation = "{\"verdict\":\"structured\"}",
        Duration = TimeSpan.FromSeconds(1),
    };
}
