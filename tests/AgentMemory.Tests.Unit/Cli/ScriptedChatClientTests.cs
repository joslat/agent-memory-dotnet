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

    /// <summary>
    /// The product sends deterministic short aliases (`s1`…`sN`) inside the batch request under
    /// `batch-source-alias-schema-v1`; the key therefore carries no session id. The stand-in must
    /// recover identity from each block's own content, exactly as a real model does, and must echo
    /// the alias back unchanged.
    /// </summary>
    private static string AliasBatch(string lab, int digits, params int[] units)
        => string.Join(
            "\n",
            units.Select((unit, index) =>
                $"<source_session key=\"s{index + 1}\">" +
                $"LAB-{lab} source {unit.ToString($"D{digits}")}: Person " +
                $"{unit.ToString($"D{digits}")} works at Company " +
                $"{unit.ToString($"D{digits}")} and prefers tea.</source_session>"));

    private static JsonDocument Respond(string prompt)
    {
        using var client = new ScriptedChatClient(
            TimeSpan.Zero,
            rules: [new ScriptedChatClient.Rule("never-match", "unused")]);
        var response = client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)])
            .GetAwaiter().GetResult();
        return JsonDocument.Parse(response.Text!);
    }

    [Fact]
    public void AliasKeyedIntegratedBatch_EchoesAliasesAndDerivesIdentityFromContent()
    {
        using var document = Respond(AliasBatch("X1", 2, 0, 1, 2, 3));
        var root = document.RootElement;

        root.GetProperty("processed_source_sessions")
            .EnumerateArray().Select(value => value.GetString())
            .Should().Equal("s1", "s2", "s3", "s4");
        root.GetProperty("entities").EnumerateArray()
            .Select(entity => entity.GetProperty("source_session").GetString())
            .Distinct().Should().BeEquivalentTo(["s1", "s2", "s3", "s4"]);
        // IntegratedLabels[0..3] — identity must equal what the pre-alias implementation produced.
        root.GetProperty("entities").EnumerateArray()
            .Select(entity => entity.GetProperty("name").GetString()!)
            .Should().Contain(["Person amber", "Company amber", "Person dahlia", "Company dahlia"]);
        root.GetProperty("facts").EnumerateArray()
            .Select(fact => fact.GetProperty("subject").GetString()!)
            .Should().Equal("Person amber", "Person birch", "Person cobalt", "Person dahlia");
    }

    [Fact]
    public void AliasKeyedBatchBatch_UsesUnitDigitsIdentity()
    {
        using var document = Respond(AliasBatch("B1", 2, 6, 7));
        var root = document.RootElement;

        root.GetProperty("processed_source_sessions")
            .EnumerateArray().Select(value => value.GetString())
            .Should().Equal("s1", "s2");
        root.GetProperty("facts").EnumerateArray()
            .Select(fact => fact.GetProperty("subject").GetString()!)
            .Should().Equal("Person 06", "Person 07");
    }

    [Fact]
    public void AliasKeyedBatch_WithoutRecoverableMarker_FailsClosed()
    {
        var act = () => Respond(
            "<source_session key=\"s1\">LAB-X1 source: no ordinal here</source_session>");

        act.Should().Throw<InvalidOperationException>(
            "a stand-in that silently returns an empty payload would read as 'learned nothing'");
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
                $"<source_session key=\"s{index + 1}\">" +
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
