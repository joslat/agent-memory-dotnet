using System.Text.Json;
using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalReportProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateAcceptedResult_SafeModeRemovesContentAndPreservesMetrics(
        bool useIdentifiers)
    {
        var evidenceDetail = useIdentifiers
            ? LongMemEvalEvidenceDetail.Identifiers
            : LongMemEvalEvidenceDetail.None;
        var result = new ExternalBenchmarkResult
        {
            BenchmarkId = "benchmark-id",
            BenchmarkName = "benchmark-name",
            OverallAccuracy = 70,
            TaskAveragedAccuracy = 69.44,
            PerTypeResults = new Dictionary<string, TypeResult>(),
            QuestionResults =
            [
                new QuestionResult
                {
                    QuestionId = "q-1",
                    QuestionType = "multi-session",
                    Question = "question-sentinel",
                    GoldAnswer = "gold-sentinel",
                    AgentResponse = "answer-sentinel",
                    Correct = true,
                    RawScore = 100,
                    JudgeExplanation = "judge-sentinel",
                    Duration = TimeSpan.FromSeconds(1)
                }
            ],
            Duration = TimeSpan.FromSeconds(2),
            TotalLlmCalls = 2,
            Options = new ExternalBenchmarkOptions()
        };

        var projection = LongMemEvalReportProjection.CreateAcceptedResult(
            result, evidenceDetail);
        var json = JsonSerializer.Serialize(projection);

        json.Should().NotContain("question-sentinel")
            .And.NotContain("gold-sentinel")
            .And.NotContain("answer-sentinel")
            .And.NotContain("judge-sentinel");
        json.Should().Contain("\"QuestionId\":\"q-1\"")
            .And.Contain("\"OverallAccuracy\":70")
            .And.Contain("\"TotalLlmCalls\":2");
    }

    [Fact]
    public void CreateAcceptedResult_ContentModeRetainsNativeForensicResult()
    {
        var result = new ExternalBenchmarkResult
        {
            BenchmarkId = "benchmark-id",
            BenchmarkName = "benchmark-name",
            OverallAccuracy = 0,
            TaskAveragedAccuracy = 0,
            PerTypeResults = new Dictionary<string, TypeResult>(),
            QuestionResults =
            [
                new QuestionResult
                {
                    QuestionId = "q-1",
                    QuestionType = "multi-session",
                    Question = "question-sentinel",
                    GoldAnswer = "gold-sentinel",
                    AgentResponse = "answer-sentinel",
                    Correct = false,
                    RawScore = 0,
                    JudgeExplanation = "judge-sentinel",
                    Duration = TimeSpan.FromSeconds(1)
                }
            ],
            Duration = TimeSpan.FromSeconds(2),
            TotalLlmCalls = 2,
            Options = new ExternalBenchmarkOptions()
        };

        var projection = LongMemEvalReportProjection.CreateAcceptedResult(
            result, LongMemEvalEvidenceDetail.Content);
        var json = JsonSerializer.Serialize(projection);

        json.Should().Contain("question-sentinel")
            .And.Contain("gold-sentinel")
            .And.Contain("answer-sentinel")
            .And.Contain("judge-sentinel")
            .And.Contain("\"Options\":");
    }

    [Fact]
    public void CreatePreparationSection_ReusedRunReportsUnperformedWorkAsNullNotZero()
    {
        // A run started with --reuse-prepared-volumes performs no preparation, so it has no batch
        // execution. This is the exact shape that crashed a live reused run: the report dereferenced
        // it through the null-forgiving operator after both evaluation arms had already succeeded.
        var section = LongMemEvalReportProjection.CreatePreparationSection(
            Manifest(),
            batchExecution: null,
            Array.Empty<LongMemEvalQuestionTelemetry>(),
            new { Calls = 0 },
            extractionCalls: 0,
            new LongMemEvalPreparationTimings(1_000, null, 20, 30, 40),
            reusedPreparedVolume: "am-lme-retained-base");

        var json = JsonSerializer.Serialize(section);

        json.Should().Contain("\"performedByThisRun\":false")
            .And.Contain("\"reusedPreparedVolume\":\"am-lme-retained-base\"");
        // Null, never 0: a zero here would be a fabricated measurement of work never performed,
        // and would let a reused run be read as a cold build that happened to be instant.
        json.Should().Contain("\"plannedEstimatedInputTokens\":null")
            .And.Contain("\"maximumObservedConcurrency\":null")
            .And.Contain("\"manifestSealAndReadBackMs\":null");
        json.Should().NotContain("\"plannedEstimatedInputTokens\":0")
            .And.NotContain("\"maximumObservedConcurrency\":0");
    }

    [Fact]
    public void CreatePreparationSection_ColdRunStillReportsRealMeasuredPreparation()
    {
        // The reuse fix must not hollow out the cold path it shares.
        var section = LongMemEvalReportProjection.CreatePreparationSection(
            Manifest(),
            new LongMemEvalPreparedBatchExecution(
                Array.Empty<LongMemEvalQuestionTelemetry>(), 121, 5_145_407, 9),
            Array.Empty<LongMemEvalQuestionTelemetry>(),
            new { Calls = 121 },
            extractionCalls: 121,
            new LongMemEvalPreparationTimings(1_000, 55.5, 20, 30, 40),
            reusedPreparedVolume: null);

        var json = JsonSerializer.Serialize(section);

        json.Should().Contain("\"performedByThisRun\":true")
            .And.Contain("\"reusedPreparedVolume\":null")
            .And.Contain("\"plannedEstimatedInputTokens\":5145407")
            .And.Contain("\"maximumObservedConcurrency\":9")
            .And.Contain("\"manifestSealAndReadBackMs\":55.5");
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
}
