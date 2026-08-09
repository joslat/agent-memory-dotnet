using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalPostRunDiagnosticsTests
{
    [Fact]
    public void OracleDiagnosticContract_IsAvailable()
    {
        typeof(LongMemEvalRunValidator).Assembly
            .GetType("AgentMemory.LongMemEval.LongMemEvalPostRunDiagnostics")
            .Should().NotBeNull(
                "M-27-V2 G2 requires an evaluator-side judge-retry and oracle diagnostic arm");
    }

    [Fact]
    public async Task RunAsync_RetriesInvalidJudgeWithoutRewritingBenchmarkResult()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var options = LongMemEvalEvidenceIndexTests.Options();
        var index = LongMemEvalEvidenceIndex.Create([entry], options);
        var question = Result(judgeExplanation: "Judge said: ");
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));

        var diagnostics = await LongMemEvalPostRunDiagnostics.RunAsync(
            chat,
            index,
            [question],
            telemetry: [],
            LongMemEvalOracleMode.None,
            judgeRetryAttempts: 1,
            retainContent: false);

        diagnostics.DiagnosticLlmCalls.Should().Be(1);
        diagnostics.JudgeRetries.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            QuestionId = "q-1",
            Status = "recovered",
            Attempts = 1,
            ValidVerdict = true,
            Correct = true,
            LlmCalls = 1
        });
        question.JudgeExplanation.Should().Be("Judge said: ");
        question.Correct.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_OracleUsesTheControlAnswerPromptContract()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var options = LongMemEvalEvidenceIndexTests.Options();
        var index = LongMemEvalEvidenceIndex.Create([entry], options);
        var calls = new List<IReadOnlyList<ChatMessage>>();
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add(call.Arg<IEnumerable<ChatMessage>>().ToArray());
                var response = calls.Count == 1 ? "two weeks" : "yes";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
            });

        var diagnostics = await LongMemEvalPostRunDiagnostics.RunAsync(
            chat,
            index,
            [Result()],
            telemetry: [],
            LongMemEvalOracleMode.All,
            judgeRetryAttempts: 0,
            retainContent: false);

        diagnostics.DiagnosticLlmCalls.Should().Be(2);
        calls.Should().HaveCount(2);
        calls[0][0].Text.Should().Be(
            "Answer the question using only the retrieved memory below. " +
            "Be concise and do not claim information that is absent from memory.");
        calls[0][1].Text.Should().StartWith("Retrieved memory:\n")
            .And.NotContain("Oracle memory:")
            .And.Contain($"\nQuestion: Current Date: {entry.QuestionDate}\n\n{entry.Question}\nAnswer:");
    }

    [Fact]
    public void Attribute_ReportsRetrievalMissWhenOraclePassesWithoutGoldEvidence()
    {
        var attribution = LongMemEvalPostRunDiagnostics.Attribute(
            Result(),
            retry: null,
            oracle: Oracle(correct: true),
            evidence: Evidence(goldSessionRecall: 0, goldTurnHit: false));

        attribution.Should().Be("retrieval-miss");
    }

    [Fact]
    public void Attribute_ReportsAnswerSynthesisWhenGoldEvidenceReachedPrompt()
    {
        var attribution = LongMemEvalPostRunDiagnostics.Attribute(
            Result(),
            retry: null,
            oracle: Oracle(correct: true),
            evidence: Evidence(goldSessionRecall: 1, goldTurnHit: true));

        attribution.Should().Be("answer-synthesis-failure");
    }

    private static QuestionResult Result(string judgeExplanation = "Judge said: no") => new()
    {
        QuestionId = "q-1",
        QuestionType = "temporal-reasoning",
        Question = "How long was the trip?",
        GoldAnswer = "two weeks",
        AgentResponse = "I do not know",
        Correct = false,
        RawScore = 0,
        JudgeExplanation = judgeExplanation,
        Duration = TimeSpan.FromSeconds(1)
    };

    private static LongMemEvalOracleResult Oracle(bool correct) => new(
        "q-1", "completed", "two weeks", true, correct, correct ? 100 : 0, 2);

    private static LongMemEvalRetrievalEvidence Evidence(
        double goldSessionRecall,
        bool goldTurnHit) => new(
            K: 30,
            AnswerPromptCharacters: 10_000,
            EstimatedAnswerPromptTokens: 2_500,
            DistinctSourceSessions: 10,
            MaxItemsFromSingleSession: 4,
            GoldSessionsRequired: 1,
            GoldSessionsHit: goldSessionRecall == 1 ? 1 : 0,
            GoldSessionRecallAtK: goldSessionRecall,
            AnnotatedGoldTurns: 1,
            GoldTurnsHit: goldTurnHit ? 1 : 0,
            GoldTurnHitAtK: goldTurnHit,
            FirstGoldSessionRank: goldSessionRecall == 1 ? 5 : null,
            FirstGoldTurnRank: goldTurnHit ? 5 : null,
            ReciprocalRank: goldSessionRecall == 1 ? 0.2 : null,
            RankedItems: []);
}