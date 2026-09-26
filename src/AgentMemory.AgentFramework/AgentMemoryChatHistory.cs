using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentMemory.AgentFramework;

/// <summary>
/// Chat history that keeps what the user and the agent said, and not the memory recalled for each turn.
/// </summary>
/// <remarks>
/// MAF's default chat history (<see cref="InMemoryChatHistoryProvider"/>) stores every request message it
/// did not load itself, which includes the memory a context provider injected. Over a session every turn's
/// recalled blocks pile up in the thread and are sent again on every later turn: measured, the third call
/// of a three-turn conversation carried 17 messages instead of 8, with duplicated turns and stale memory.
/// </remarks>
public static class AgentMemoryChatHistory
{
    /// <summary>
    /// The storage filter: keeps only the messages the caller sent (source <see cref="AgentRequestMessageSourceType.External"/>).
    /// <see cref="Neo4jChatHistoryProvider"/> stores through this same filter.
    /// </summary>
    public static IEnumerable<ChatMessage> ExcludeInjectedContext(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Where(message => message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);
    }

    /// <summary>
    /// An <see cref="InMemoryChatHistoryProvider"/> that does not store injected memory. Pass it as
    /// <c>ChatClientAgentOptions.ChatHistoryProvider</c> next to an AgentMemory context provider.
    /// </summary>
    /// <param name="options">Further options (a chat reducer, a state key…); its storage filter is set here.</param>
    public static InMemoryChatHistoryProvider CreateInMemoryProvider(InMemoryChatHistoryProviderOptions? options = null)
    {
        options ??= new InMemoryChatHistoryProviderOptions();
        options.StorageInputRequestMessageFilter = ExcludeInjectedContext;
        return new InMemoryChatHistoryProvider(options);
    }
}
