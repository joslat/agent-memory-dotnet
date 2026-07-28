using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalRunValidatorTests
{
    [Fact]
    public void Validate_AcceptsCompleteRunWithoutEmbeddedErrors()
    {
        var validation = LongMemEvalRunValidator.Validate(
            questionCount: 1,
            llmCalls: 2,
            telemetry: [new LongMemEvalQuestionTelemetry(1, 20, 10, false)],
            questionResults: [Result("q-1")]);

        validation.Accepted.Should().BeTrue();
        validation.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsAgentFailureAndIncompleteCallAccounting()
    {
        var results = Enumerable.Range(1, 10)
            .Select(index => index == 5
                ? Result("q-5", "[ERROR: HTTP 429]", "Skipped due to error: HTTP 429")
                : Result($"q-{index}"))
            .ToArray();
        var telemetry = Enumerable.Range(1, 9)
            .Select(index => new LongMemEvalQuestionTelemetry(index, 20, 10, false))
            .ToArray();

        var validation = LongMemEvalRunValidator.Validate(
            questionCount: 10,
            llmCalls: 18,
            telemetry,
            results);

        validation.Accepted.Should().BeFalse();
        validation.Issues.Should().Contain(issue => issue.Contains("20", StringComparison.Ordinal));
        validation.Issues.Should().Contain(issue => issue.Contains("q-5", StringComparison.Ordinal));
        validation.Issues.Should().Contain(issue => issue.Contains("telemetry", StringComparison.OrdinalIgnoreCase));
        validation.Issues.Should().NotContain(issue => issue.Contains("429", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsJudgeErrorEvenWhenCallCountIsComplete()
    {
        var result = Result("q-judge", judgeExplanation: "Judge error: HTTP 400 unsupported temperature");

        var validation = LongMemEvalRunValidator.Validate(
            questionCount: 1,
            llmCalls: 2,
            telemetry: [new LongMemEvalQuestionTelemetry(1, 20, 10, false)],
            questionResults: [result]);

        validation.Accepted.Should().BeFalse();
        validation.Issues.Should().ContainSingle()
            .Which.Should().Contain("q-judge");
    }

    [Fact]
    public void Classify_ReportsSanitizedAdapterStage()
    {
        var result = Result("q-stage", "[ERROR: LongMemEval storage stage failed.]");
        LongMemEvalRunValidator.Classify(result).Should().Be("storage-error");
    }

    [Fact]
    public void Classify_PrefersDirectAdapterStageOverGenericAgentError()
    {
        var result = Result("q-stage", "[ERROR: Agent invocation failed.]");
        var telemetry = new LongMemEvalQuestionTelemetry(1, 20, 10, false, "answer-error");

        LongMemEvalRunValidator.Classify(result, telemetry).Should().Be("answer-error");
    }
    private static QuestionResult Result(
        string questionId,
        string agentResponse = "answer",
        string judgeExplanation = "Judge said: yes") => new()
        {
            QuestionId = questionId,
            QuestionType = "multi-session",
            Question = "question",
            GoldAnswer = "answer",
            AgentResponse = agentResponse,
            Correct = true,
            RawScore = 100,
            JudgeExplanation = judgeExplanation,
            Duration = TimeSpan.FromSeconds(1)
        };
}
