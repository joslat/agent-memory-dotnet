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
    public void The_in_memory_provider_stores_through_the_filter_and_keeps_other_options()
    {
        var options = new InMemoryChatHistoryProviderOptions { StateKey = "custom" };

        var provider = AgentMemoryChatHistory.CreateInMemoryProvider(options);

        provider.Should().NotBeNull();
        options.StorageInputRequestMessageFilter.Should().NotBeNull();
        options.StateKey.Should().Be("custom");
        var recalled = new ChatMessage(ChatRole.System, "memory")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "AgentMemory");
        options.StorageInputRequestMessageFilter!([recalled]).Should().BeEmpty();
    }
}
