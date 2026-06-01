using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using AgentMemory.Extraction.Llm.Internal;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Task 3.5: tolerant JSON parsing and parse-failure re-prompting in the shared
/// <see cref="LlmExtractionRunner"/>. The tolerant-parse helpers are tested directly; the retry
/// contract is tested end-to-end through <see cref="LlmEntityExtractor"/>.
/// </summary>
public sealed class LlmExtractionRunnerTests
{
    private static readonly Message SampleMessage = new()
    {
        MessageId = "m-1",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = "Alice works at Acme Corp.",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private const string ValidEntityJson =
        """{"entities":[{"name":"Alice","type":"PERSON","confidence":0.9}]}""";

    private static LlmEntityExtractor CreateExtractor(IChatClient client, int maxRetries)
    {
        var options = new LlmExtractionOptions { MaxRetries = maxRetries };
        return new LlmEntityExtractor(client, Options.Create(options), NullLogger<LlmEntityExtractor>.Instance);
    }

    private static IChatClient ClientReturning(params string[] responses)
    {
        var client = Substitute.For<IChatClient>();
        var tasks = responses
            .Select(r => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, r))))
            .ToArray();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(tasks[0], tasks.Skip(1).ToArray());
        return client;
    }

    // ---- ExtractJson: tolerant extraction ----

    [Fact]
    public void ExtractJson_StripsJsonCodeFence()
    {
        var raw = "```json\n" + ValidEntityJson + "\n```";
        LlmExtractionRunner.ExtractJson(raw).Should().Be(ValidEntityJson);
    }

    [Fact]
    public void ExtractJson_StripsPlainCodeFence()
    {
        var raw = "```\n" + ValidEntityJson + "\n```";
        LlmExtractionRunner.ExtractJson(raw).Should().Be(ValidEntityJson);
    }

    [Fact]
    public void ExtractJson_LocatesObjectInsideProse()
    {
        var raw = $"Sure! Here is what I found:\n{ValidEntityJson}\nLet me know if you need more.";
        LlmExtractionRunner.ExtractJson(raw).Should().Be(ValidEntityJson);
    }

    [Fact]
    public void ExtractJson_ReturnsNullWhenNoContainer()
    {
        LlmExtractionRunner.ExtractJson("I could not find anything.").Should().BeNull();
        LlmExtractionRunner.ExtractJson("   ").Should().BeNull();
        LlmExtractionRunner.ExtractJson(null).Should().BeNull();
    }

    // ---- TryParse ----

    [Fact]
    public void TryParse_FencedValidJson_Succeeds()
    {
        var ok = LlmExtractionRunner.TryParse("```json\n" + ValidEntityJson + "\n```", out var dto);
        ok.Should().BeTrue();
        dto!.Entities.Should().ContainSingle().Which.Name.Should().Be("Alice");
    }

    [Fact]
    public void TryParse_Garbage_Fails()
    {
        LlmExtractionRunner.TryParse("not json at all", out _).Should().BeFalse();
        LlmExtractionRunner.TryParse("{ this is : broken", out _).Should().BeFalse();
    }

    // ---- End-to-end: tolerance + retry contract ----

    [Fact]
    public async Task ExtractAsync_FencedValidJson_ReturnsParsedResults()
    {
        var client = ClientReturning("```json\n" + ValidEntityJson + "\n```");
        var sut = CreateExtractor(client, maxRetries: 0);

        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().ContainSingle().Which.Name.Should().Be("Alice");
        await client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_InvalidThenValid_RetriesAndReturnsResults()
    {
        // MaxRetries=1 → up to 2 attempts. First is garbage, second is valid.
        var client = ClientReturning("not parseable", ValidEntityJson);
        var sut = CreateExtractor(client, maxRetries: 1);

        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().ContainSingle().Which.Name.Should().Be("Alice");
        await client.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_AlwaysInvalid_ReturnsEmptyAfterMaxRetriesWithoutThrowing()
    {
        // MaxRetries=2 → exactly 3 attempts, all invalid → empty, no throw.
        var client = ClientReturning("garbage", "still garbage", "nope");
        var sut = CreateExtractor(client, maxRetries: 2);

        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
        await client.Received(3).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
    }
}
