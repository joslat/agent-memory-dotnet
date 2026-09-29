using System.Text.RegularExpressions;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// <c>--judge-max-output-tokens</c> lifts the judge's 256-token ceiling for reasoning judges, and leaves
/// it at 256 when absent.
/// </summary>
/// <remarks>
/// GLM-5.3-Flash reasoned on every extraction call of the 2026-09-29 checkpoint, about 55–60% of its
/// output. A judge from the same family, capped at 256, can spend the whole budget before it writes a
/// verdict, and an empty verdict rejects the arm.
/// </remarks>
public sealed class JudgeOutputCeilingTests : IDisposable
{
    private readonly string _dataset = Path.GetTempFileName();

    public void Dispose() => File.Delete(_dataset);

    [Fact]
    public void AbsentTheCeilingIsThe256EveryRecordedRunUsed()
    {
        LongMemEvalBenchmarkProtocol.CreateOptions("d.json", 10, 42, 2, LongMemEvalEvidenceDetail.Identifiers, 30)
            .JudgeMaxOutputTokens.Should().Be(256);
        LongMemEvalPreparedPairProgram.Parse(["--prepared-pair"]).JudgeMaxOutputTokens.Should().Be(256);
        LongMemEvalReferenceArmProgram.CreateBenchmarkOptions(
                LongMemEvalReferenceArmProgram.Parse(["--reference-arm", "no-memory", "--dataset", _dataset]))
            .JudgeMaxOutputTokens.Should().Be(256);
    }

    [Fact]
    public void TheFlagReachesAgentEvalsJudgeOptions()
    {
        LongMemEvalReferenceArmProgram.CreateBenchmarkOptions(
                LongMemEvalReferenceArmProgram.Parse(
                    ["--reference-arm", "full-history", "--dataset", _dataset, "--judge-max-output-tokens", "4096"]))
            .JudgeMaxOutputTokens.Should().Be(4096);
        LongMemEvalPreparedPairProgram.Parse(["--prepared-pair", "--judge-max-output-tokens", "4096"])
            .JudgeMaxOutputTokens.Should().Be(4096);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void ANonPositiveOrNonNumericCeilingIsRefused(string value)
    {
        var act = () => LongMemEvalSamplingOptions.ParseJudgeMaxOutputTokens(value);

        act.Should().Throw<ArgumentException>().WithMessage("*--judge-max-output-tokens*");
    }

    /// <summary>
    /// The prepared pair builds judge options twice: for the draw, and for a --question-ids subset.
    /// </summary>
    [Fact]
    public void BothPreparedPairOptionSitesCarryTheCeiling() =>
        Regex.Matches(ToolSource("LongMemEvalPreparedPairProgram.cs"),
                @"judgeMaxOutputTokens: options\.JudgeMaxOutputTokens")
            .Count.Should().Be(2);

    private static string ToolSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", fileName));
    }
}
