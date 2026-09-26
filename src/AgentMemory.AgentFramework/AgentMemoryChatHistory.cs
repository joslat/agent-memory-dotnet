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
    /// <param name="options">
    /// Further options (a chat reducer, a state key…). Not modified: a copy is used. A storage filter of
    /// your own is kept and runs after this one, on what remains.
    /// </param>
    public static InMemoryChatHistoryProvider CreateInMemoryProvider(InMemoryChatHistoryProviderOptions? options = null)
    {
        var own = options?.StorageInputRequestMessageFilter;
        var copy = new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = options?.ChatReducer,
            ReducerTriggerEvent = options?.ReducerTriggerEvent ?? new InMemoryChatHistoryProviderOptions().ReducerTriggerEvent,
            StateKey = options?.StateKey,
            StateInitializer = options?.StateInitializer,
            JsonSerializerOptions = options?.JsonSerializerOptions,
            ProvideOutputMessageFilter = options?.ProvideOutputMessageFilter,
            StorageInputResponseMessageFilter = options?.StorageInputResponseMessageFilter,
            StorageInputRequestMessageFilter = StorageFilter(own),
        };
        return new InMemoryChatHistoryProvider(copy);
    }

    /// <summary>The storage filter: injected context out first, then the caller's own filter (if any).</summary>
    internal static Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>> StorageFilter(
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? own) =>
        own is null ? ExcludeInjectedContext : messages => own(ExcludeInjectedContext(messages));
}
