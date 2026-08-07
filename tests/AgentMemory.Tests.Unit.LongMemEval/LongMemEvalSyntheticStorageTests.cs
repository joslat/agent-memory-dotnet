using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G3B.9 root fix. AgentEval's <c>LongMemEvalHistoryFormatter</c> cannot express session structure
/// through an injection API that accepts only (user, assistant) pairs, so it fabricates a turn per
/// session boundary: the user "says" <c>--- Session 12 (date) ---</c> and the assistant "replies"
/// <c>Understood. Starting a new conversation session.</c> Persisting those put 21% redundant corpus
/// into memory whose <b>identical</b> content produced identical embeddings — 46 byte-identical
/// copies in one question — which tie on cosine and monopolise top-K together.
/// </summary>
public sealed class LongMemEvalSyntheticStorageTests
{
    [Fact]
    public void FabricatedSessionBoundaryTurnsAreNotPersisted()
    {
        var messages = Messages("real-user", "boundary", "ack", "real-assistant");
        var origins = Origins(
            ("m0", false, false),
            ("m1", true, false),   // the fabricated session marker
            ("m2", false, true),   // its fabricated acknowledgement
            ("m3", false, false));

        var persisted = AgentMemoryLongMemEvalAdapter.SelectPersistableMessages(messages, origins);

        Assert.Equal(["m0", "m3"], persisted.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void RealConversationIsNeverDropped()
    {
        // Removing fabrication is the goal; losing a real turn would be far worse than the flooding
        // this prevents.
        var messages = Messages("a", "b", "c", "d");
        var origins = Origins(
            ("m0", false, false), ("m1", false, false),
            ("m2", false, false), ("m3", false, false));

        var persisted = AgentMemoryLongMemEvalAdapter.SelectPersistableMessages(messages, origins);

        Assert.Equal(4, persisted.Count);
    }

    [Fact]
    public void AnUnclassifiableMessageIsKept()
    {
        // Dropping what we cannot identify would silently lose evidence. Keep it and let retrieval
        // decide, exactly as the retrieval-side filter already does.
        var messages = Messages("a", "b");
        var origins = Origins(("m0", false, false));

        var persisted = AgentMemoryLongMemEvalAdapter.SelectPersistableMessages(messages, origins);

        Assert.Contains(persisted, m => m.MessageId == "m1");
    }

    [Fact]
    public void WithNoProvenanceAtAllEverythingIsPersisted()
    {
        // No evidence index means no way to tell fabrication from conversation, so nothing is removed.
        var messages = Messages("a", "b");

        var persisted = AgentMemoryLongMemEvalAdapter.SelectPersistableMessages(
            messages, new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal));

        Assert.Equal(2, persisted.Count);
    }

    private static List<Message> Messages(params string[] contents) =>
        contents.Select((content, index) => new Message
        {
            MessageId = $"m{index}",
            SessionId = "s",
            ConversationId = "s",
            Role = index % 2 == 0 ? "user" : "assistant",
            Content = content,
            TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(index)
        }).ToList();

    private static Dictionary<string, LongMemEvalMessageOrigin> Origins(
        params (string Id, bool Boundary, bool Padding)[] items) =>
        items.ToDictionary(
            item => item.Id,
            item => new LongMemEvalMessageOrigin(
                0, "session-1", 0, 0, "2023/05/20 (Sat) 10:19", "user",
                item.Id, item.Boundary, item.Padding, false),
            StringComparer.Ordinal);
}
