using System.Text.Json.Nodes;
using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// <c>--rejudge</c> reads the judge options the run recorded and the answers it kept, and counts agreement
/// the same way whatever report shape it reads.
/// </summary>
public sealed class RejudgeTests
{
    private const string Sha = "d6f21ea9d60a0d56f34a05b609c79c88a451d2ae03597821ea3d5a9678c3a442";

    [Fact]
    public void ThePreparedPairsJudgeOptionsComeFromItsFingerprint()
    {
        var stored = LongMemEvalRejudgeProgram.ReadStoredJudge(Report("""
            "fingerprint": { "judgeModel": "zai-org/GLM-5.3@bitdeer", "datasetSha256": "SHA",
              "judgeVerdictProtocol": "StructuredJson", "judgeMaxOutputTokens": 4096, "judgeRetryAttempts": 2 }
            """));

        stored.Should().Be(new LongMemEvalRejudgeProgram.StoredJudge(
            "zai-org/GLM-5.3@bitdeer", Sha, JudgeVerdictProtocol.StructuredJson, 4096, 2));
    }

    /// <summary>The reference arm records its ceiling only inside its judgeRequest label.</summary>
    [Fact]
    public void AReferenceArmsCeilingIsReadFromItsJudgeRequestLabel()
    {
        var stored = LongMemEvalRejudgeProgram.ReadStoredJudge(Report("""
            "fingerprint": { "judgeModel": "j", "datasetSha256": "SHA", "judgeVerdictProtocol": "StructuredJson",
              "judgeRequest": "AgentEval-source-native-null-temperature-4096-tokens", "judgeRetryAttempts": 2 }
            """));

        stored.JudgeMaxOutputTokens.Should().Be(4096);
    }

    /// <summary>Re-judging under options the run never recorded would compare two different judges' settings.</summary>
    [Theory]
    [InlineData("""{ "judgeModel": "j", "datasetSha256": "SHA", "judgeMaxOutputTokens": 4096 }""")]
    [InlineData("""{ "judgeModel": "j", "datasetSha256": "SHA", "judgeVerdictProtocol": "StructuredJson" }""")]
    [InlineData("""{ "judgeModel": "j", "judgeVerdictProtocol": "StructuredJson", "judgeMaxOutputTokens": 4096 }""")]
    public void AMissingJudgeOptionFailsClosed(string fingerprint)
    {
        var act = () => LongMemEvalRejudgeProgram.ReadStoredJudge(
            Report($"\"fingerprint\": {fingerprint.Replace("SHA", Sha, StringComparison.Ordinal)}"));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void EveryArmOfAPreparedPairIsRead_AndAnswerlessJudgmentsAreSkipped()
    {
        var arms = LongMemEvalRejudgeProgram.ReadArms(Report("""
            "arms": {
              "structured": { "judgments": [
                { "QuestionId": "q1", "Correct": true, "agentResponse": "Lyon" },
                { "QuestionId": "q2", "Correct": false } ] },
              "hybrid": { "judgments": [ { "QuestionId": "q1", "Correct": false, "agentResponse": "Paris" } ] } }
            """));

        arms.Select(arm => (arm.Name, arm.Items.Count)).Should().Equal(("structured", 1), ("hybrid", 1));
        arms[0].Items[0].Should().Be(new LongMemEvalRejudgeProgram.StoredItem("q1", true, "Lyon"));
    }

    /// <summary>
    /// Review: a recorded inconclusive verdict (Correct null) was read with GetValue&lt;bool&gt;() and crashed the run; an
    /// agent error or a history skipped for the context window was sent to the judge and counted in agreement.
    /// </summary>
    [Fact]
    public void AnInconclusiveRecordIsKept_AndAnswersNoJudgeCanGradeAreCountedApart()
    {
        var arms = LongMemEvalRejudgeProgram.ReadArms(Report($$"""
            "arms": { "structured": { "judgments": [
                { "QuestionId": "q1", "Correct": null, "agentResponse": "Lyon" },
                { "QuestionId": "q2", "Correct": false, "agentResponse": "[ERROR: the provider timed out]" },
                { "QuestionId": "q3", "Correct": false, "agentResponse": "{{LongMemEvalReferenceAgent.SkippedAnswer}}" },
                { "QuestionId": "q4", "Correct": true, "agentResponse": "Paris" } ] } }
            """));

        arms.Should().ContainSingle();
        arms[0].Items.Select(item => (item.QuestionId, item.Correct)).Should().Equal(("q1", (bool?)null), ("q4", true));
        arms[0].NotJudgeable.Should().Be(2);
    }

    [Fact]
    public void AMisspelledArmFails_InsteadOfDroppingSilently()
    {
        IReadOnlyList<LongMemEvalRejudgeProgram.StoredArm> all = [new("structured", []), new("hybrid", [])];

        LongMemEvalRejudgeProgram.SelectArms(all, ["Structured"]).Should().ContainSingle().Which.Name.Should().Be("structured");
        var act = () => LongMemEvalRejudgeProgram.SelectArms(all, ["structured", "hybird"]);
        act.Should().Throw<ArgumentException>().WithMessage("*hybird*structured, hybrid*");
    }

    [Fact]
    public void EachJudgeGetsItsOwnDefaultFile()
    {
        var report = Path.Combine("runs", "prepared-pair-report.json");
        var a = LongMemEvalRejudgeProgram.DefaultDestination(report, "zai-org/GLM-5.3@bitdeer");
        var b = LongMemEvalRejudgeProgram.DefaultDestination(report, "deepseek/DeepSeek-V4-Flash@bitdeer");

        Path.GetFileName(a).Should().Be("prepared-pair-report-rejudge-zai-org-GLM-5.3-bitdeer.json");
        a.Should().NotBe(b);
    }

    [Fact]
    public void AReferenceArmsJudgmentsAreReadUnderItsArmName()
    {
        var arms = LongMemEvalRejudgeProgram.ReadArms(Report("""
            "referenceArm": { "arm": "FullHistory" },
            "judgments": [ { "QuestionId": "q1", "Correct": true, "agentResponse": "Lyon" } ]
            """));

        arms.Should().ContainSingle().Which.Name.Should().Be("FullHistory");
    }

    [Fact]
    public void AgreementLeavesInconclusiveVerdictsOut_AndListsTheDisagreements()
    {
        var agreement = LongMemEvalRejudgeProgram.ArmAgreement.From("structured",
        [
            Item("q1", recorded: true, @new: true),
            Item("q2", recorded: true, @new: false),
            Item("q3", recorded: false, @new: false),
            Item("q4", recorded: false, @new: null),
        ]);

        agreement.Compared.Should().Be(3);
        agreement.Agreed.Should().Be(2);
        agreement.NewInconclusive.Should().Be(1);
        agreement.RecordedCorrect.Should().Be(2);
        agreement.NewCorrect.Should().Be(1);
        agreement.Disagreements.Should().ContainSingle().Which.QuestionId.Should().Be("q2");
    }

    [Fact]
    public void AnInconclusiveRecordIsLeftOutOfAgreement()
    {
        var agreement = LongMemEvalRejudgeProgram.ArmAgreement.From("structured",
        [
            Item("q1", recorded: true, @new: true),
            Item("q2", recorded: null, @new: false),
        ], notRejudged: 3);

        agreement.Compared.Should().Be(1);
        agreement.RecordedInconclusive.Should().Be(1);
        agreement.NotRejudged.Should().Be(3);
        agreement.Disagreements.Should().BeEmpty();
    }

    /// <summary>Known values: perfect agreement is 1, chance-level agreement is 0.</summary>
    [Fact]
    public void KappaMatchesKnownValues()
    {
        LongMemEvalRejudgeProgram.ArmAgreement.Kappa([(true, true), (false, false), (true, true), (false, false)])
            .Should().Be(1);
        LongMemEvalRejudgeProgram.ArmAgreement.Kappa([(true, true), (true, false), (false, true), (false, false)])
            .Should().Be(0);
        // 20 pairs: 8 yes/yes, 2 yes/no, 1 no/yes, 9 no/no -> observed 0.85, expected 0.5, kappa 0.70.
        var pairs = Enumerable.Repeat((true, true), 8).Concat(Enumerable.Repeat((true, false), 2))
            .Concat(Enumerable.Repeat((false, true), 1)).Concat(Enumerable.Repeat((false, false), 9)).ToArray();
        LongMemEvalRejudgeProgram.ArmAgreement.Kappa(pairs).Should().BeApproximately(0.70, 0.001);
    }

    /// <summary>Both report kinds keep the answers now, through one projection, so either can be re-judged.</summary>
    [Fact]
    public void TheJudgmentProjectionKeepsAnswersUnlessEvidenceIsSuppressed()
    {
        QuestionResult[] results =
        [
            new()
            {
                QuestionId = "q1", QuestionType = "single-session-user", Question = "Where do I live?",
                GoldAnswer = "Lyon", AgentResponse = "Lyon", Correct = true, RawScore = 100,
            },
        ];

        LongMemEvalJudgmentProjection.Project(results, LongMemEvalEvidenceDetail.None).Should().BeNull();
        LongMemEvalJudgmentProjection.Project(results, LongMemEvalEvidenceDetail.Identifiers).Should().ContainSingle();
    }

    [Theory]
    [InlineData("LongMemEvalPreparedPairProgram.cs")]
    [InlineData("LongMemEvalReferenceArmProgram.cs")]
    public void BothReportsWriteJudgmentsThroughTheSharedProjection(string file)
    {
        ToolSource(file).Should().Contain("judgments = LongMemEvalJudgmentProjection.Project(");
    }

    private static LongMemEvalRejudgeProgram.RejudgedItem Item(string id, bool? recorded, bool? @new) =>
        new(id, "single-session-user", recorded, @new, @new is null ? "Inconclusive" : "Yes", null);

    private static JsonObject Report(string body) =>
        JsonNode.Parse("{" + body.Replace("\"SHA\"", $"\"{Sha}\"", StringComparison.Ordinal) + "}")!.AsObject();

    private static string ToolSource(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "tools", "AgentMemory.LongMemEval", file));
    }
}
