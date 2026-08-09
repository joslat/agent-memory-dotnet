using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using MemoryFact = AgentMemory.Abstractions.Domain.Fact;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalAgentEvalEvidenceTests
{
    [Xunit.Fact]
    public void Build_RawMessagePreservesExactTurnScoreTimestampAndPromptOrder()
    {
        var message = new Message
        {
            MessageId = "message-1",
            SessionId = "run-session",
            ConversationId = "run-session",
            Role = "user",
            Content = "I stayed in Japan for two weeks.",
            TimestampUtc = DateTimeOffset.UnixEpoch
        };
        var context = new MemoryContext
        {
            SessionId = "run-session",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantMessages = new MemoryContextSection<Message>
            {
                Items = [message],
                RankedItems = [new MemoryContextRankedItem("message-1", 0.875, 1, 1)]
            }
        };
        var origins = new Dictionary<string, LongMemEvalMessageOrigin>
        {
            ["message-1"] = Origin(
                ordinal: 0,
                sessionId: "source-session-1",
                turn: 3,
                timestamp: "2024/01/01 (Mon) 10:00")
        };

        var envelope = LongMemEvalAgentEvalEvidence.Build(
            context, origins, LongMemEvalEvidenceDetail.Identifiers);

        var retrieved = envelope.Retrieved.Should().ContainSingle().Subject;
        retrieved.Id.Should().Be("message-1");
        retrieved.Rank.Should().Be(1);
        retrieved.SimilarityScore.Should().Be(0.875);
        retrieved.SourceSessionId.Should().Be("source-session-1");
        retrieved.SourceTurnIndex.Should().Be(3);
        retrieved.SourceTimestamp.Should().Be(
            new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero));
        retrieved.AnswerContextOrder.Should().BeNull();
        retrieved.Content.Should().BeNull();
        var answer = envelope.AnswerContext.Should().ContainSingle().Subject;
        answer.AnswerContextOrder.Should().Be(1);
        answer.Content.Should().BeNull();
    }

    [Xunit.Fact]
    public void Build_StructuredWholeSessionReportsSessionButDoesNotInventDecisiveTurn()
    {
        var context = new MemoryContext
        {
            SessionId = "run-session",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantFacts = new MemoryContextSection<MemoryFact>
            {
                Items =
                [
                    new MemoryFact
                    {
                        FactId = "fact-1",
                        Subject = "user",
                        Predicate = "stayed_in",
                        Object = "Japan for two weeks",
                        Confidence = 0.9,
                        SourceMessageIds = ["message-1", "message-2"],
                        CreatedAtUtc = DateTimeOffset.UnixEpoch
                    }
                ],
                RankedItems = [new MemoryContextRankedItem("fact-1", 0.75, 1, 1)]
            }
        };
        var origins = new Dictionary<string, LongMemEvalMessageOrigin>
        {
            ["message-1"] = Origin(
                ordinal: 0,
                sessionId: "source-session-1",
                turn: 0,
                timestamp: "2024/01/01 (Mon) 10:00"),
            ["message-2"] = Origin(
                ordinal: 1,
                sessionId: "source-session-1",
                turn: 1,
                timestamp: "2024/01/01 (Mon) 10:00")
        };

        var envelope = LongMemEvalAgentEvalEvidence.Build(
            context, origins, LongMemEvalEvidenceDetail.Identifiers);

        var reference = envelope.Retrieved.Should().ContainSingle().Subject;
        reference.Id.Should().Be("fact:fact-1");
        reference.SimilarityScore.Should().Be(0.75);
        reference.SourceSessionId.Should().Be("source-session-1");
        reference.SourceTurnIndex.Should().BeNull(
            "the extractor assigns the whole source session to each learned item");
        reference.SourceTimestamp.Should().BeNull(
            "no single source turn is attributable without using evaluator gold labels");
    }


    [Xunit.Fact]
    public void Build_SyntheticBoundaryRetainsContextButCannotSatisfyGoldSessionEvidence()
    {
        var message = new Message
        {
            MessageId = "boundary-1",
            SessionId = "run-session",
            ConversationId = "run-session",
            Role = "user",
            Content = "--- Session 1 ---",
            TimestampUtc = DateTimeOffset.UnixEpoch
        };
        var context = new MemoryContext
        {
            SessionId = "run-session",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantMessages = new MemoryContextSection<Message>
            {
                Items = [message]
            }
        };
        var origins = new Dictionary<string, LongMemEvalMessageOrigin>
        {
            ["boundary-1"] = new(
                MessageOrdinal: 0,
                SourceSessionId: "source-session-1",
                SourceSessionOrdinal: 0,
                SourceTurnOrdinal: null,
                SourceTimestamp: "2024/01/01 (Mon) 10:00",
                Role: "user",
                FormattedContent: "--- Session 1 ---",
                IsSyntheticBoundary: true,
                IsSyntheticFormatterPadding: false,
                HasAnswer: false)
        };

        var envelope = LongMemEvalAgentEvalEvidence.Build(
            context, origins, LongMemEvalEvidenceDetail.Identifiers);

        var reference = envelope.Retrieved.Should().ContainSingle().Subject;
        reference.Id.Should().Be("boundary-1");
        reference.SourceSessionId.Should().BeNull();
        reference.SourceTurnIndex.Should().BeNull();
        reference.SourceTimestamp.Should().BeNull();
    }
    private static LongMemEvalMessageOrigin Origin(
        int ordinal,
        string sessionId,
        int turn,
        string timestamp) =>
        new(
            MessageOrdinal: ordinal,
            SourceSessionId: sessionId,
            SourceSessionOrdinal: 0,
            SourceTurnOrdinal: turn,
            SourceTimestamp: timestamp,
            Role: turn % 2 == 0 ? "user" : "assistant",
            FormattedContent: $"content-{ordinal}",
            IsSyntheticBoundary: false,
            IsSyntheticFormatterPadding: false,
            HasAnswer: false);
}
