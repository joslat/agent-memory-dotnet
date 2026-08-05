using System.Text.Json;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalPreparationManifestTests
{
    [Fact]
    public void Create_IsDeterministicAndContentFree()
    {
        var first = Manifest();
        var second = Manifest();

        first.Fingerprint.Should().Be(second.Fingerprint);
        var json = JsonSerializer.Serialize(
            first,
            LongMemEvalPreparationManifest.JsonOptions);
        json.Should().NotContain("secret question text");
        json.Should().NotContain("secret gold answer");
        json.Should().NotContain("secret model answer");
        json.Should().NotContain("secret recalled content");
        json.Should().NotContain("credential");
        json.Should().NotContain("endpoint");
    }

    [Fact]
    public void VerifyIntegrity_RejectsChangedBudget()
    {
        var tampered = Manifest() with { MaxRelevantMessages = 31 };

        var act = tampered.VerifyIntegrity;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*fingerprint*");
    }

    [Theory]
    [InlineData("dataset")]
    [InlineData("model")]
    [InlineData("budget")]
    [InlineData("response-format")]
    [InlineData("unified")]
    [InlineData("multi-session")]
    [InlineData("workers")]
    [InlineData("sessions-per-batch")]
    [InlineData("input-tokens")]
    public void PreparedState_RejectsChangedConfiguration(string field)
    {
        var manifest = Manifest();
        var expected = Expectation();
        expected = field switch
        {
            "dataset" => expected with { DatasetSha256 = "different-dataset" },
            "model" => expected with { ExtractionModelId = "different-model" },
            "budget" => expected with { MaxRelevantMessages = 31 },
            "response-format" => expected with { UseJsonResponseFormat = false },
            "unified" => expected with { UseUnifiedExtraction = false },
            "multi-session" => expected with { UseMultiSessionBatchExtraction = false },
            "workers" => expected with { PreparationWorkers = 9 },
            "sessions-per-batch" => expected with { MaxSessionsPerBatch = 3 },
            "input-tokens" => expected with { MaxInputTokens = 99_999 },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var act = () => new LongMemEvalPreparedState(
            manifest,
            "prepared-run",
            expected);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*configuration*");
    }

    [Fact]
    public void PreparedState_AcceptsExactConfiguration()
    {
        var act = () => new LongMemEvalPreparedState(
            Manifest(),
            "prepared-run",
            Expectation());

        act.Should().NotThrow();
    }

    private static LongMemEvalPreparationManifest Manifest() =>
        LongMemEvalPreparationManifest.Create(
            "preparation-1",
            "dataset-sha256",
            "agenteval-revision",
            "prepared-run",
            "answer-model",
            "judge-model",
            "extraction-model",
            "embedding-model",
            1536,
            30,
            "metadata-only-not-in-extraction-prompt",
            [
                new LongMemEvalPreparedQuestion(
                    1,
                    "q-1",
                    "history-sha256",
                    LongMemEvalPreparationManifest.Hash(
                        "prepared-run-session-0001|prepared-run-owner-0001"),
                    614,
                    52,
                    52,
                    new LongMemEvalGraphSnapshot(2, 3, 4, 1, 9, 9, 20, 6, 1))
            ],
            208,
            useJsonResponseFormat: true,
            useUnifiedExtraction: true,
            useMultiSessionBatchExtraction: true,
            preparationWorkers: 10,
            maxSessionsPerBatch: 4,
            maxInputTokens: 100_000);

    private static LongMemEvalPreparationExpectation Expectation() =>
        LongMemEvalPreparationFingerprint.Expect(
            "dataset-sha256",
            "agenteval-revision",
            "answer-model",
            "judge-model",
            "extraction-model",
            "embedding-model",
            1536,
            30,
            useJsonResponseFormat: true,
            useUnifiedExtraction: true,
            useMultiSessionBatchExtraction: true,
            preparationWorkers: 10,
            maxSessionsPerBatch: 4,
            maxInputTokens: 100_000);
}
