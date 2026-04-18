using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Observability;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Observability;

public sealed class InstrumentedFactExtractorTests
{
    private readonly IFactExtractor _inner = Substitute.For<IFactExtractor>();
    private readonly MemoryMetrics _metrics = new();
    private readonly InstrumentedFactExtractor _sut;

    public InstrumentedFactExtractorTests()
    {
        _sut = new InstrumentedFactExtractor(_inner, _metrics);
    }

    [Fact]
    public async Task ExtractAsync_DelegatesToInner()
    {
        var messages = new List<Message> { CreateMessage("Alice works at Neo4j") };
        var expected = new List<ExtractedFact>
        {
            new() { Subject = "Alice", Predicate = "works_at", Object = "Neo4j", Confidence = 0.95 }
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
        _inner.ExtractAsync(messages, Arg.Any<CancellationToken>()).Returns(new List<ExtractedFact>());

        var result = await _sut.ExtractAsync(messages);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        var act = () => new InstrumentedFactExtractor(null!, _metrics);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullMetrics_Throws()
    {
        var act = () => new InstrumentedFactExtractor(_inner, null!);
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
