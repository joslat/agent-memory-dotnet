using AgentMemory.Cli.Perf;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class ScriptedChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_RuleWithSecondMatch_RequiresBothMarkers()
    {
        using var client = new ScriptedChatClient(
            TimeSpan.Zero,
            rules: [new ScriptedChatClient.Rule("entity extraction", "matched", "LAB-E0 source")]);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "entity extraction only")]);

        response.Text.Should().Be(ScriptedChatClient.EmptyPayload);
    }

    [Fact]
    public async Task GetResponseAsync_RuleWithSecondMatch_SelectsPayloadWhenBothMarkersExist()
    {
        using var client = new ScriptedChatClient(
            TimeSpan.Zero,
            rules: [new ScriptedChatClient.Rule("entity extraction", "matched", "LAB-E0 source")]);

        var response = await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "entity extraction only"),
            new ChatMessage(ChatRole.User, "LAB-E0 source"),
        ]);

        response.Text.Should().Be("matched");
    }
}
