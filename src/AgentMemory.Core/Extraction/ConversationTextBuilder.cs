using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

public static class ConversationTextBuilder
{
    public static string Build(IReadOnlyList<Message> messages)
        => string.Join("\n", messages.Select(m => $"{m.Role}: {m.Content}"));
}
