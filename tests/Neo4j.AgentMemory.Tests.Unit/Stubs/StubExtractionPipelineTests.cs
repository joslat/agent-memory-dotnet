using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Stubs;

namespace Neo4j.AgentMemory.Tests.Unit.Stubs;

public class StubExtractionPipelineTests
{
    private static Message MakeMessage(string id = "msg-1") => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "session-1",
        Role = "user",
        Content = "Hello world",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static StubExtractionPipeline BuildPipeline() =>
        new(
            new StubEntityExtractor(NullLogger<StubEntityExtractor>.Instance),
            new StubFactExtractor(NullLogger<StubFactExtractor>.Instance),
            new StubPreferenceExtractor(NullLogger<StubPreferenceExtractor>.Instance),
            new StubRelationshipExtractor(NullLogger<StubRelationshipExtractor>.Instance),
            NullLogger<StubExtractionPipeline>.Instance);

    [Fact]
    public async Task ExtractAsync_ReturnsEmptyEntities()
    {
        var pipeline = BuildPipeline();
        var request = new ExtractionRequest
        {
            Messages = new[] { MakeMessage() },
            SessionId = "s1"
        };

        var result = await pipeline.ExtractAsync(request);

        result.Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_ReturnsEmptyFacts()
    {
        var pipeline = BuildPipeline();
        var request = new ExtractionRequest
        {
            Messages = new[] { MakeMessage() },
            SessionId = "s1"
        };

        var result = await pipeline.ExtractAsync(request);

        result.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_ReturnsEmptyPreferences()
    {
        var pipeline = BuildPipeline();
        var request = new ExtractionRequest
        {
            Messages = new[] { MakeMessage() },
            SessionId = "s1"
        };

        var result = await pipeline.ExtractAsync(request);

        result.Preferences.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_ReturnsEmptyRelationships()
    {
        var pipeline = BuildPipeline();
        var request = new ExtractionRequest
        {
            Messages = new[] { MakeMessage() },
            SessionId = "s1"
        };

        var result = await pipeline.ExtractAsync(request);

        result.Relationships.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_PopulatesSourceMessageIds()
    {
        var pipeline = BuildPipeline();
        var msg1 = MakeMessage("msg-a");
        var msg2 = MakeMessage("msg-b");
        var request = new ExtractionRequest
        {
            Messages = new[] { msg1, msg2 },
            SessionId = "s1"
        };

        var result = await pipeline.ExtractAsync(request);

        result.SourceMessageIds.Should().BeEquivalentTo("msg-a", "msg-b");
    }

    [Fact]
    public async Task ExtractAsync_RespectsExtractionTypeFlags()
    {
        var pipeline = BuildPipeline();
        var request = new ExtractionRequest
        {
            Messages = new[] { MakeMessage() },
            SessionId = "s1",
            TypesToExtract = ExtractionTypes.None
        };

        var result = await pipeline.ExtractAsync(request);

        result.Entities.Should().BeEmpty();
        result.Facts.Should().BeEmpty();
        result.Preferences.Should().BeEmpty();
        result.Relationships.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MetadataContainsStubFlag()
    {
        var pipeline = BuildPipeline();
        var request = new ExtractionRequest
        {
            Messages = new[] { MakeMessage() },
            SessionId = "s1"
        };

        var result = await pipeline.ExtractAsync(request);

        result.Metadata.Should().ContainKey("stub")
            .WhoseValue.Should().Be(true);
    }
}
