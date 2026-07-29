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
}
