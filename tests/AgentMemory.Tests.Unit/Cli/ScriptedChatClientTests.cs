using System.Runtime.InteropServices;
using System.Text.Json;
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

    [Fact]
    public async Task GetResponseAsync_IntegratedCapacityLabels_AreVectorDistinctAtFixedDimensions()
    {
        using var client = new ScriptedChatClient(
            TimeSpan.Zero,
            rules: [new ScriptedChatClient.Rule("never-match", "unused")]);
        var sourceSessions = string.Join(
            "\n",
            Enumerable.Range(0, 320).Select(index =>
                $"<source_session key=\"capacity-session-{index:D3}\">" +
                $"LAB-N1 source {index:D3}</source_session>"));

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, sourceSessions)]);

        using var document = JsonDocument.Parse(response.Text!);
        var personNames = document.RootElement
            .GetProperty("entities")
            .EnumerateArray()
            .Select(entity => entity.GetProperty("name").GetString()!)
            .Where(name => name.StartsWith("Person ", StringComparison.Ordinal))
            .ToArray();
        var vectorKeys = personNames
            .Select(name => DeterministicEmbeddingGenerator.Vector(name, 384))
            .Select(vector => Convert.ToHexString(MemoryMarshal.AsBytes(vector.AsSpan())))
            .ToArray();
        var maxFuzzyScore = personNames
            .SelectMany((left, index) => personNames.Skip(index + 1)
                .Select(right => FuzzySharp.Fuzz.TokenSortRatio(left, right)))
            .Max();

        personNames.Should().HaveCount(320);
        vectorKeys.Distinct(StringComparer.Ordinal).Should().HaveCount(320);
    }
}
