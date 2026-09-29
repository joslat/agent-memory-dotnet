using System.Text.RegularExpressions;
using AgentMemory.Inference;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Every report names the model that actually judged it.
/// </summary>
/// <remarks>
/// <para>
/// All three report sites stamped <c>judgeModel = deployment</c>, the ANSWER model's identity. That was
/// true while one Azure deployment served both roles. Once <c>AI_JUDGE_*</c> could point the judge
/// elsewhere, every report claimed the subject had graded itself. The sealed manifest was fixed earlier
/// (<see cref="ManifestRecordsEachRoleSeparatelyTests"/>); the reports, one layer out, were not.
/// </para>
/// <para>
/// A source test, for the reason that one gives: the sites are anonymous-object initialisers inside
/// long entry points, and a wrong string there compiles, runs and produces a plausible artifact.
/// </para>
/// </remarks>
public sealed class ReportJudgeIdentityTests
{
    [Theory]
    [InlineData("Program.cs")]
    [InlineData("LongMemEvalPreparedPairProgram.cs")]
    [InlineData("LongMemEvalReferenceArmProgram.cs")]
    public void EveryReportStampsTheJudgesOwnIdentity(string file)
    {
        var stamps = Regex.Matches(ToolSource(file), @"judgeModel\s*=\s*([^,\r\n]+)")
            .Select(match => match.Groups[1].Value.Trim())
            .ToArray();

        stamps.Should().NotBeEmpty($"{file} writes a report with a judgeModel field");
        stamps.Should().OnlyContain(value => value == "model.JudgeIdentity",
            "the report must name the judge, not the model it judged");
    }

    /// <summary>The identity those sites now read differs from the answer model's when the judge does.</summary>
    [Fact]
    public void WithAJudgeOverrideTheJudgeIdentityIsNotTheAnswerIdentity()
    {
        var resolution = InferenceProviderEnvironment.Resolve(name => name switch
        {
            "AI_INFERENCE_PROVIDER" => "bitdeer",
            "BITDEER_API_KEY" => "test-key",
            "AI_JUDGE_PROVIDER" => "bitdeer",
            "AI_JUDGE_ENDPOINT" => "https://api-inference.bitdeer.ai/v1",
            "AI_JUDGE_API_KEY" => "test-key",
            "AI_JUDGE_MODEL" => "zai-org/GLM-5.3",
            _ => null,
        });
        var model = HarnessClients.ForSettings(resolution.Settings!);

        model.AnswerIdentity.Should().Be("zai-org/GLM-5.3-Flash@bitdeer");
        model.JudgeIdentity.Should().Be("zai-org/GLM-5.3@bitdeer");
    }

    private static string ToolSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", fileName));
    }
}
