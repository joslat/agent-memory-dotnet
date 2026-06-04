using FluentAssertions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;

namespace AgentMemory.Tests.Unit.Services;

public sealed class MemoryContextFormatterTests
{
    private static RecallResult BuildResult(RetrievalBlendMode mode)
    {
        var msg = new Message
        {
            MessageId = "m1", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "hello-memory", TimestampUtc = DateTimeOffset.UtcNow
        };
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UtcNow,
            RecentMessages = new MemoryContextSection<Message> { Items = [msg] },
            GraphRagContext = "graph-knowledge",
            BlendMode = mode
        };
        return new RecallResult { Context = context, TotalItemsRetrieved = 2 };
    }

    [Fact]
    public void FormatRecallResult_Blended_RendersGraphContextAfterMessages()
    {
        var text = MemoryContextFormatter.FormatRecallResult(BuildResult(RetrievalBlendMode.Blended));

        text.IndexOf("### Recent Messages", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("### Graph Context", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatRecallResult_GraphRagThenMemory_RendersGraphContextBeforeMessages()
    {
        var text = MemoryContextFormatter.FormatRecallResult(BuildResult(RetrievalBlendMode.GraphRagThenMemory));

        text.IndexOf("### Graph Context", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("### Recent Messages", StringComparison.Ordinal));
    }
}
