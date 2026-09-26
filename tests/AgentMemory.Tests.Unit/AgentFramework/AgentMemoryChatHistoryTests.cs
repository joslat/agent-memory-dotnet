using AgentMemory.AgentFramework;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>A10: the stored conversation keeps what was said, not the memory recalled for each turn.</summary>
public sealed class AgentMemoryChatHistoryTests
{
    [Fact]
    public void Only_the_callers_messages_are_kept()
    {
        var said = new ChatMessage(ChatRole.User, "Priya called me.");
        var recalled = new ChatMessage(ChatRole.System, "<recalled_memory category=\"facts\">…</recalled_memory>")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "AgentMemory");
        var replayed = new ChatMessage(ChatRole.User, "an earlier turn")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory);

        AgentMemoryChatHistory.ExcludeInjectedContext([said, recalled, replayed]).Should().Equal(said);
    }

    [Fact]
    public void The_callers_options_are_not_modified()
    {
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>> own = messages => messages;
        var options = new InMemoryChatHistoryProviderOptions { StateKey = "custom", StorageInputRequestMessageFilter = own };

        AgentMemoryChatHistory.CreateInMemoryProvider(options).Should().NotBeNull();

        options.StorageInputRequestMessageFilter.Should().BeSameAs(own);
        options.StateKey.Should().Be("custom");
    }

    [Fact]
    public void A_filter_of_the_callers_own_is_kept_and_runs_on_what_remains()
    {
        var said = new ChatMessage(ChatRole.User, "my card number is 4111");
        var tool = new ChatMessage(ChatRole.Tool, "tool output");
        var recalled = new ChatMessage(ChatRole.System, "memory")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "AgentMemory");
        // The host's own rule: never store tool output.
        var filter = AgentMemoryChatHistory.StorageFilter(messages => messages.Where(m => m.Role != ChatRole.Tool));

        filter([said, tool, recalled]).Should().Equal(said);
    }
}
