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
/// 36.3: a preference is the user's own stated taste. Measured live: a taught book's characters' tastes and
/// a request ("Recommend some music…") were stored as the user's preferences. Every extractor carries the
/// same instruction or none (a setting honoured by some extractors makes memory depend on a performance flag),
/// and off, every prompt is byte-for-byte what it was.
/// </summary>
public sealed class OwnPreferencesInstructionTests
{
    private const string Marker = "A preference is the user's own taste";

    private static readonly Message Sample = new()
    {
        MessageId = "m-1", ConversationId = "c-1", SessionId = "s-1", Role = "user",
        Content = "My brother hates cilantro.", TimestampUtc = DateTimeOffset.UtcNow,
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
    public void The_unified_prompts_carry_it_exactly_when_on(bool on)
    {
        var unified = LlmUnifiedMemoryExtractor.BuildSystemPrompt(
            AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch,
            ownPreferencesOnly: on);
        var multiSession = LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(null, ownPreferencesOnly: on);

        unified.Contains(Marker, StringComparison.Ordinal).Should().Be(on);
        multiSession.Contains(Marker, StringComparison.Ordinal).Should().Be(on);
    }

    [Fact]
    public void Off_the_unified_prompt_is_what_it_was()
    {
        LlmUnifiedMemoryExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch)
            .Should().Be(LlmUnifiedMemoryExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch,
                ownPreferencesOnly: false));
    }

    [Fact]
    public async Task The_unified_extractor_sends_it_when_the_option_is_on()
    {
        var (client, captured) = Client("""{"entities":[],"facts":[],"preferences":[],"relations":[]}""");
        var sut = new LlmUnifiedMemoryExtractor(client,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, OwnPreferencesOnly = true }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        await sut.ExtractAsync([Sample]);

        captured[0].First(m => m.Role == ChatRole.System).Text.Should().Contain(Marker);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_preference_extractor_sends_it_exactly_when_on(bool on)
    {
        var (client, captured) = Client("""{"preferences":[]}""");
        var sut = new LlmPreferenceExtractor(client,
            Options.Create(new LlmExtractionOptions { OwnPreferencesOnly = on }), NullLogger<LlmPreferenceExtractor>.Instance);

        await sut.ExtractAsync([Sample]);

        var system = captured[0].First(m => m.Role == ChatRole.System).Text;
        system.Contains(Marker, StringComparison.Ordinal).Should().Be(on);
        if (!on) system.Should().Be(LlmPreferenceExtractor.DefaultSystemPrompt);
    }

    /// <summary>36.6 (D-8d): a question's presupposition is not stated ("When did I move to Lyon?" was stored as a move).</summary>
    [Fact]
    public void The_questions_instruction_says_a_presupposition_is_not_stated()
    {
        ExtractionPromptSemantics.QuestionsInstruction(true).Should().Contain("takes for granted");
        ExtractionPromptSemantics.QuestionsInstruction(false).Should().BeEmpty();
    }
}
