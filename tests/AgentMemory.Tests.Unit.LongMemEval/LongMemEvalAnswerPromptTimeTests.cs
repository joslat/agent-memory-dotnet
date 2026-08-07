using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G3B.2. LongMemEval session dates live only in AgentEval's boundary markers, which G3B.1 drops as
/// formatter boilerplate — so the filtered arms carried no time information at all and every
/// temporal question became unanswerable. These guard the restored signal.
/// </summary>
public sealed class LongMemEvalAnswerPromptTimeTests
{
    private const string QuestionDate = "2023/06/03 (Sat) 15:47";

    [Fact]
    public void RecalledMessagesCarryTheirSourceTimestampIntoTheAnswerPrompt()
    {
        // Without this, "20 titles" and "currently 25" are indistinguishable and knowledge-update
        // questions cannot be answered even when both turns were retrieved.
        var context = ContextWith(
            Message("m1", "2023/05/20 (Sat) 10:19", "I have 20 titles to watch."),
            Message("m2", "2023/05/22 (Mon) 03:27", "My to-watch list is currently 25."));

        var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(context, "How many?", QuestionDate);

        Assert.Contains("2023/05/20 (Sat) 10:19", prompt, StringComparison.Ordinal);
        Assert.Contains("2023/05/22 (Mon) 03:27", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCurrentDateReachesTheAnswerPrompt()
    {
        // "How many days ago did I attend a networking event?" is unanswerable without it, however
        // good retrieval is.
        var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
            ContextWith(Message("m1", "2022/03/09 (Wed) 12:08", "Just back from a networking event.")),
            "How many days ago?",
            QuestionDate);

        Assert.Contains(QuestionDate, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageWithoutASourceTimestampStillRendersRatherThanBeingDropped()
    {
        // Absent provenance must degrade to the stored clock, never silently remove evidence.
        var message = new Message
        {
            MessageId = "m1",
            SessionId = "s",
            ConversationId = "s",
            Role = "user",
            Content = "no provenance here",
            TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(7)
        };

        var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
            ContextWith(message), "Question?", QuestionDate);

        Assert.Contains("no provenance here", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimestampComesFromRecalledMetadataNotFromEvaluatorSideKnowledge()
    {
        // The point of the fix: AgentMemory already returns this through recall, so the harness is
        // restoring product data rather than injecting answers it happens to know.
        var message = Message("m1", "2023/05/22 (Mon) 03:27", "content");
        Assert.True(message.Metadata.ContainsKey("sourceTimestamp"));

        var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
            ContextWith(message), "Question?", QuestionDate);

        Assert.Contains("2023/05/22 (Mon) 03:27", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReferenceHistoryOverloadCarriesTimestampsToo()
    {
        // The fairness rule: the full-history baseline is fixed in the same commit, or the
        // comparison measures the fix rather than the memory system.
        var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
            [("user", "2023/05/20 (Sat) 10:19", "twenty"), ("user", "2023/05/22 (Mon) 03:27", "twenty five")],
            "How many?",
            QuestionDate);

        Assert.Contains("2023/05/20 (Sat) 10:19", prompt, StringComparison.Ordinal);
        Assert.Contains("2023/05/22 (Mon) 03:27", prompt, StringComparison.Ordinal);
        Assert.Contains(QuestionDate, prompt, StringComparison.Ordinal);
    }

    private static Message Message(string id, string sourceTimestamp, string content) => new()
    {
        MessageId = id,
        SessionId = "s",
        ConversationId = "s",
        Role = "user",
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
        Metadata = new Dictionary<string, object> { ["sourceTimestamp"] = sourceTimestamp }
    };

    private static MemoryContext ContextWith(params Message[] messages) => new()
    {
        SessionId = "s",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        RelevantMessages = new MemoryContextSection<Message> { Items = messages }
    };
}
