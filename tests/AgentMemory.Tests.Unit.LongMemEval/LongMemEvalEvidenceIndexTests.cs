using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalEvidenceIndexTests
{
    [Fact]
    public void Resolve_AlignsAgentEvalBoundaryAndSourceTurnsWithoutGoldLeakage()
    {
        var entry = Entry();
        var options = Options();
        var formatted = LongMemEvalHistoryFormatter.Format(entry, options);
        var index = LongMemEvalEvidenceIndex.Create([entry], options);

        var resolved = index.Resolve(formatted, InvocationPrompt(entry));

        resolved.QuestionId.Should().Be("q-1");
        resolved.Messages.Should().HaveCount(4);
        resolved.Messages.Take(2).Should().OnlyContain(message => message.IsSyntheticBoundary);
        resolved.Messages[2].Should().BeEquivalentTo(new
        {
            SourceSessionId = "session-1",
            SourceTurnOrdinal = (int?)0,
            SourceTimestamp = "2024/01/01 (Mon) 10:00",
            HasAnswer = true
        });
        resolved.AnswerSessionIds.Should().ContainSingle().Which.Should().Be("session-1");
        index.GetByQuestionId("q-1").Should().BeSameAs(resolved);
    }

    [Fact]
    public void Create_AlignsOddSessionWhenAgentEvalDropsLeadingAssistantTurn()
    {
        var entry = Entry();
        entry.HaystackSessions =
        [
            [
                new LongMemEvalTurn
                {
                    Role = "assistant",
                    Content = "Prior assistant-only preamble.",
                    HasAnswer = false
                },
                new LongMemEvalTurn
                {
                    Role = "user",
                    Content = "I stayed in Japan for two weeks.",
                    HasAnswer = true
                },
                new LongMemEvalTurn
                {
                    Role = "assistant",
                    Content = "That sounds memorable.",
                    HasAnswer = false
                }
            ]
        ];
        var options = Options();
        var formatted = LongMemEvalHistoryFormatter.Format(entry, options);

        var resolved = LongMemEvalEvidenceIndex.Create([entry], options)
            .Resolve(formatted, InvocationPrompt(entry));

        resolved.Messages.Should().HaveCount(4);
        resolved.Messages.Select(message => message.SourceTurnOrdinal).Should()
            .Equal(null, null, 1, 2);
    }

    [Fact]
    public void Create_AlignsTrailingUserTurnWithAgentEvalSyntheticAssistant()
    {
        var entry = Entry();
        entry.HaystackSessions =
        [
            [
                new LongMemEvalTurn
                {
                    Role = "user",
                    Content = "I stayed in Japan for two weeks.",
                    HasAnswer = true
                },
                new LongMemEvalTurn
                {
                    Role = "assistant",
                    Content = "That sounds memorable.",
                    HasAnswer = false
                },
                new LongMemEvalTurn
                {
                    Role = "user",
                    Content = "I returned home yesterday.",
                    HasAnswer = false
                }
            ]
        ];
        var options = Options();
        var formatted = LongMemEvalHistoryFormatter.Format(entry, options);

        var resolved = LongMemEvalEvidenceIndex.Create([entry], options)
            .Resolve(formatted, InvocationPrompt(entry));

        resolved.Messages.Should().HaveCount(6);
        resolved.Messages.Select(message => message.SourceTurnOrdinal).Should()
            .Equal(null, null, 0, 1, 2, null);
        resolved.Messages[^1].IsSyntheticFormatterPadding.Should().BeTrue();
    }

    [Fact]
    public void Resolve_MatchesAgentEvalCurrentDateInvocationPrompt()
    {
        var entry = Entry();
        var options = Options();
        var formatted = LongMemEvalHistoryFormatter.Format(entry, options);
        var index = LongMemEvalEvidenceIndex.Create([entry], options);
        var invocationPrompt = $"Current Date: {entry.QuestionDate}\n\n{entry.Question}";

        var resolved = index.Resolve(formatted, invocationPrompt);

        resolved.Question.Should().Be(entry.Question);
    }

    [Fact]
    public void Resolve_RejectsMutatedHistoryBeforeItCanBePersisted()
    {
        var entry = Entry();
        var options = Options();
        var formatted = LongMemEvalHistoryFormatter.Format(entry, options).ToArray();
        formatted[^1] = (formatted[^1].UserMessage + " mutated", formatted[^1].AssistantResponse);
        var index = LongMemEvalEvidenceIndex.Create([entry], options);

        var act = () => index.Resolve(formatted, InvocationPrompt(entry));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not match*");
    }

    [Fact]
    public void Build_ComputesGoldRecallRanksAndOmitsContentByDefault()
    {
        var entry = Entry();
        var options = Options();
        var formatted = LongMemEvalHistoryFormatter.Format(entry, options);
        var question = LongMemEvalEvidenceIndex.Create([entry], options)
            .Resolve(formatted, InvocationPrompt(entry));
        var recalled = question.Messages.Select((origin, index) => new Message
        {
            MessageId = $"m-{index}",
            SessionId = "evaluation-session",
            ConversationId = "evaluation-session",
            Role = origin.Role,
            Content = origin.FormattedContent,
            TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(index)
        }).ToArray();
        var ranked = recalled.Select((message, index) => new MemoryContextRankedItem(
            message.MessageId,
            Score: 1d - index / 10d,
            RetrievalRank: index + 1,
            ContextRank: index + 1)).ToArray();
        var origins = recalled.Select((message, index) => (message.MessageId, question.Messages[index]))
            .ToDictionary(item => item.MessageId, item => item.Item2, StringComparer.Ordinal);

        var evidence = LongMemEvalRetrievalEvidence.Build(
            question, recalled, ranked, origins, LongMemEvalEvidenceDetail.Identifiers,
            answerPromptCharacters: 400, configuredMessageBudget: 30);

        evidence.GoldAttributionObservable.Should().BeTrue();
        evidence.K.Should().Be(4);
        evidence.AnswerPromptCharacters.Should().Be(400);
        evidence.EstimatedAnswerPromptTokens.Should().Be(100);
        evidence.GoldSessionRecallAtK.Should().Be(1);
        evidence.GoldTurnHitAtK.Should().BeTrue();
        evidence.FirstGoldSessionRank.Should().Be(3);
        evidence.FirstGoldTurnRank.Should().Be(3);
        evidence.ReciprocalRank.Should().BeApproximately(1d / 3d, 0.000001);
        evidence.DistinctSourceSessions.Should().Be(1);
        evidence.RankedItems.Should().HaveCount(4)
            .And.OnlyContain(item => item.Content == null);
    }

    internal static LongMemEvalEntry Entry() => new()
    {
        QuestionId = "q-1",
        QuestionType = "temporal-reasoning",
        Question = "How long was the trip?",
        AnswerRaw = JsonDocument.Parse("\"two weeks\"").RootElement.Clone(),
        QuestionDate = "2024/02/01 (Thu) 10:00",
        HaystackSessionIds = ["session-1"],
        HaystackDates = ["2024/01/01 (Mon) 10:00"],
        AnswerSessionIds = ["session-1"],
        HaystackSessions =
        [
            [
                new LongMemEvalTurn
                {
                    Role = "user",
                    Content = "I stayed in Japan for two weeks.",
                    HasAnswer = true
                },
                new LongMemEvalTurn
                {
                    Role = "assistant",
                    Content = "That sounds memorable.",
                    HasAnswer = false
                }
            ]
        ]
    };

    internal static string InvocationPrompt(LongMemEvalEntry entry) =>
        string.IsNullOrEmpty(entry.QuestionDate)
            ? entry.Question
            : $"Current Date: {entry.QuestionDate}\n\n{entry.Question}";
    internal static ExternalBenchmarkOptions Options() => new()
    {
        MaxQuestions = 1,
        StratifiedSampling = false,
        PreserveSessionBoundaries = true,
        IncludeTimestamps = true,
        HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
        DatasetMode = "S"
    };
}