using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Observability;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Observability;

public sealed class InstrumentedRelationshipExtractorTests
{
    private readonly IRelationshipExtractor _inner = Substitute.For<IRelationshipExtractor>();
    private readonly MemoryMetrics _metrics = new();
    private readonly InstrumentedRelationshipExtractor _sut;

    public InstrumentedRelationshipExtractorTests()
    {
        _sut = new InstrumentedRelationshipExtractor(_inner, _metrics);
    }

    [Fact]
    public async Task ExtractAsync_DelegatesToInner()
    {
        var messages = new List<Message> { CreateMessage("Alice knows Bob") };
        var expected = new List<ExtractedRelationship>
        {
            new() { SourceEntity = "Alice", TargetEntity = "Bob", RelationshipType = "KNOWS", Confidence = 0.9 }
        };
        _inner.ExtractAsync(messages, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.ExtractAsync(messages);

        result.Should().BeSameAs(expected);
        await _inner.Received(1).ExtractAsync(messages, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_OnException_Rethrows()
    {
        var messages = new List<Message> { CreateMessage("Hello") };
        _inner.ExtractAsync(messages, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => _sut.ExtractAsync(messages);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task ExtractAsync_EmptyResult_ReturnsEmptyList()
    {
        var messages = new List<Message> { CreateMessage("Nothing") };
        _inner.ExtractAsync(messages, Arg.Any<CancellationToken>()).Returns(new List<ExtractedRelationship>());

        var result = await _sut.ExtractAsync(messages);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        var act = () => new InstrumentedRelationshipExtractor(null!, _metrics);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullMetrics_Throws()
    {
        var act = () => new InstrumentedRelationshipExtractor(_inner, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static Message CreateMessage(string content) =>
        new()
        {
            MessageId = Guid.NewGuid().ToString(),
            SessionId = "s1",
            ConversationId = "c1",
            Role = "user",
            Content = content,
            TimestampUtc = DateTimeOffset.UtcNow
        };
}
