using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// J-14: who an event was shared with is kept as facts of its own. Found in simulated conversations: "hiking in Sintra
/// with my friend Pedro" was kept whole in one run and split without Pedro in the next, so "who was with me?" had no
/// answer. Every extractor carries the same instruction or none, and off, every prompt is byte-for-byte what it was.
/// </summary>
public sealed class EventCompanionsInstructionTests
{
    private const string Marker = "did something WITH other people";

    private static readonly Message Sample = new()
    {
        MessageId = "m-1", ConversationId = "c-1", SessionId = "s-1", Role = "user",
        Content = "Yesterday I went hiking in Sintra with my friend Pedro.", TimestampUtc = DateTimeOffset.UtcNow,
    };

    private static (IChatClient Client, List<IEnumerable<ChatMessage>> Captured) Client(string json)
    {
        var client = Substitute.For<IChatClient>();
        var captured = new List<IEnumerable<ChatMessage>>();
        client.GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(captured.Add), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));
        return (client, captured);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_prompt_builder_carries_it_exactly_when_on(bool on)
    {
        LlmUnifiedMemoryExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch, eventCompanions: on)
            .Contains(Marker, StringComparison.Ordinal).Should().Be(on);
        LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(null, eventCompanions: on)
            .Contains(Marker, StringComparison.Ordinal).Should().Be(on);
        LlmFactExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch, eventCompanions: on)
            .Contains(Marker, StringComparison.Ordinal).Should().Be(on);
    }

    [Fact]
    public void Off_every_prompt_is_what_it_was()
    {
        LlmUnifiedMemoryExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch)
            .Should().Be(LlmUnifiedMemoryExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch, eventCompanions: false));
        LlmFactExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch)
            .Should().Be(LlmFactExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch, eventCompanions: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_unified_extractor_sends_it_exactly_when_the_option_is_on(bool on)
    {
        var (client, captured) = Client("""{"entities":[],"facts":[],"preferences":[],"relations":[]}""");
        var sut = new LlmUnifiedMemoryExtractor(client,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, CaptureEventCompanions = on }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        await sut.ExtractAsync([Sample]);

        captured[0].First(m => m.Role == ChatRole.System).Text.Contains(Marker, StringComparison.Ordinal).Should().Be(on);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_fact_extractor_sends_it_exactly_when_the_option_is_on(bool on)
    {
        var (client, captured) = Client("""{"facts":[]}""");
        var sut = new LlmFactExtractor(client,
            Options.Create(new LlmExtractionOptions { CaptureEventCompanions = on }), NullLogger<LlmFactExtractor>.Instance);

        await sut.ExtractAsync([Sample]);

        captured[0].First(m => m.Role == ChatRole.System).Text.Contains(Marker, StringComparison.Ordinal).Should().Be(on);
    }
}
