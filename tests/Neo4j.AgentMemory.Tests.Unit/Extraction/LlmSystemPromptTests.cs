using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.Llm;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

public sealed class LlmSystemPromptTests
{
    private static readonly Message SampleMessage = new()
    {
        MessageId = "m-1",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = "Alice works at Acme.",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static (IChatClient client, List<IEnumerable<ChatMessage>> captured) SetupClient(string json = """{"entities":[]}""")
    {
        var client = Substitute.For<IChatClient>();
        var captured = new List<IEnumerable<ChatMessage>>();
        client.GetResponseAsync(
            Arg.Do<IEnumerable<ChatMessage>>(msgs => captured.Add(msgs)),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));
        return (client, captured);
    }

    // ── Entity extractor ───────────────────────────────────────────────────────

    [Fact]
    public async Task EntityExtractor_DefaultPromptUsed_WhenOptionIsNull()
    {
        var (client, captured) = SetupClient();
        var options = new LlmExtractionOptions { EntityExtractionPrompt = null };
        var sut = new LlmEntityExtractor(client, Options.Create(options), NullLogger<LlmEntityExtractor>.Instance);

        await sut.ExtractAsync(new[] { SampleMessage });

        var systemMsg = captured[0].First(m => m.Role == ChatRole.System);
        systemMsg.Text.Should().Be(LlmEntityExtractor.DefaultSystemPrompt);
    }

    [Fact]
    public async Task EntityExtractor_CustomPromptUsed_WhenOptionIsSet()
    {
        var (client, captured) = SetupClient();
        const string customPrompt = "CUSTOM ENTITY PROMPT";
        var options = new LlmExtractionOptions { EntityExtractionPrompt = customPrompt };
        var sut = new LlmEntityExtractor(client, Options.Create(options), NullLogger<LlmEntityExtractor>.Instance);

        await sut.ExtractAsync(new[] { SampleMessage });

        var systemMsg = captured[0].First(m => m.Role == ChatRole.System);
        systemMsg.Text.Should().Be(customPrompt);
    }

    // ── Fact extractor ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FactExtractor_DefaultPromptUsed_WhenOptionIsNull()
    {
        var (client, captured) = SetupClient("""{"facts":[]}""");
        var options = new LlmExtractionOptions { FactExtractionPrompt = null };
        var sut = new LlmFactExtractor(client, Options.Create(options), NullLogger<LlmFactExtractor>.Instance);

        await sut.ExtractAsync(new[] { SampleMessage });

        var systemMsg = captured[0].First(m => m.Role == ChatRole.System);
        systemMsg.Text.Should().Be(LlmFactExtractor.DefaultSystemPrompt);
    }

    [Fact]
    public async Task FactExtractor_CustomPromptUsed_WhenOptionIsSet()
    {
        var (client, captured) = SetupClient("""{"facts":[]}""");
        const string customPrompt = "CUSTOM FACT PROMPT";
        var options = new LlmExtractionOptions { FactExtractionPrompt = customPrompt };
        var sut = new LlmFactExtractor(client, Options.Create(options), NullLogger<LlmFactExtractor>.Instance);

        await sut.ExtractAsync(new[] { SampleMessage });

        var systemMsg = captured[0].First(m => m.Role == ChatRole.System);
        systemMsg.Text.Should().Be(customPrompt);
    }

    // ── Relationship extractor ─────────────────────────────────────────────────

    [Fact]
    public async Task RelationshipExtractor_DefaultPromptUsed_WhenOptionIsNull()
    {
        var (client, captured) = SetupClient("""{"relations":[]}""");
        var options = new LlmExtractionOptions { RelationshipExtractionPrompt = null };
        var sut = new LlmRelationshipExtractor(client, Options.Create(options), NullLogger<LlmRelationshipExtractor>.Instance);

        await sut.ExtractAsync(new[] { SampleMessage });

        var systemMsg = captured[0].First(m => m.Role == ChatRole.System);
        systemMsg.Text.Should().Be(LlmRelationshipExtractor.DefaultSystemPrompt);
    }

    [Fact]
    public async Task RelationshipExtractor_CustomPromptUsed_WhenOptionIsSet()
    {
        var (client, captured) = SetupClient("""{"relations":[]}""");
        const string customPrompt = "CUSTOM RELATIONSHIP PROMPT";
        var options = new LlmExtractionOptions { RelationshipExtractionPrompt = customPrompt };
        var sut = new LlmRelationshipExtractor(client, Options.Create(options), NullLogger<LlmRelationshipExtractor>.Instance);

        await sut.ExtractAsync(new[] { SampleMessage });

        var systemMsg = captured[0].First(m => m.Role == ChatRole.System);
        systemMsg.Text.Should().Be(customPrompt);
    }

    // ── Preference extractor ───────────────────────────────────────────────────

    [Fact]
    public async Task PreferenceExtractor_DefaultPromptUsed_WhenOptionIsNull()
    {
        var (client, captured) = SetupClient("""{"preferences":[]}""");
        var options = new LlmExtractionOptions { PreferenceExtractionPrompt = null };
        var sut = new LlmPreferenceExtractor(client, Options.Create(options), NullLogger<LlmPreferenceExtractor>.Instance);

        await sut.ExtractAsync(new[] { SampleMessage });

        var systemMsg = captured[0].First(m => m.Role == ChatRole.System);
        systemMsg.Text.Should().Be(LlmPreferenceExtractor.DefaultSystemPrompt);
    }

    [Fact]
    public async Task PreferenceExtractor_CustomPromptUsed_WhenOptionIsSet()
    {
        var (client, captured) = SetupClient("""{"preferences":[]}""");
        const string customPrompt = "CUSTOM PREFERENCE PROMPT";
        var options = new LlmExtractionOptions { PreferenceExtractionPrompt = customPrompt };
        var sut = new LlmPreferenceExtractor(client, Options.Create(options), NullLogger<LlmPreferenceExtractor>.Instance);

        await sut.ExtractAsync(new[] { SampleMessage });

        var systemMsg = captured[0].First(m => m.Role == ChatRole.System);
        systemMsg.Text.Should().Be(customPrompt);
    }
}
