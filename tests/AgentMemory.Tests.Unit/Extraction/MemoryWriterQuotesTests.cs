using AgentMemory.Core.Extraction;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// #249 and #250: the writer's quotes are checked against the message, and what it does not keep is recorded with a reason.
/// </summary>
public sealed class MemoryWriterQuotesTests
{
    private const string Message = "The wedding is in Bariloche on Saturday, December 12.\nI already booked my flights: I'll be there from the 9th.";

    [Fact]
    public void AnExactQuoteIsFoundAndItsOffsetsReResolveToTheMessage()
    {
        var span = MemoryWriterQuotes.Locate(Message, "booked my flights")!.Value;

        Message[span.Start..span.End].Should().Be("booked my flights");
    }

    [Fact]
    public void AQuoteAcrossALineBreakIsFound()
    {
        var span = MemoryWriterQuotes.Locate(Message, "December 12. I already booked")!.Value;

        Message[span.Start..span.End].Should().Be("December 12.\nI already booked");
    }

    [Fact]
    public void ATypographicApostropheMatchesAStraightOne()
    {
        var span = MemoryWriterQuotes.Locate(Message, "I’ll be there")!.Value;

        Message[span.Start..span.End].Should().Be("I'll be there");
    }

    [Fact]
    public void ACaseFoldedQuoteIsFound() =>
        MemoryWriterQuotes.Locate(Message, "THE WEDDING IS IN BARILOCHE").Should().NotBeNull();

    [Theory]
    [InlineData("The wedding is in Mendoza")]
    [InlineData("I booked a hotel")]
    [InlineData("   ")]
    public void AQuoteTheMessageDoesNotContainIsNotFound(string quote) =>
        MemoryWriterQuotes.Locate(Message, quote).Should().BeNull();

    [Fact]
    public void AReplyWithInvalidOperationsKeepsTheValidOneAndRecordsTheOthersWithTheirReasons()
    {
        const string reply = """
            {"ops": [
              {"op": "add", "kind": "fact", "text": "Bruna | will be in | Bariloche", "quote": "I'll be there"},
              {"op": "remember", "text": "Bruna | likes | mate"},
              {"op": "add", "kind": "fact"},
              {"op": "confirm"},
              "not an object"
            ]}
            """;

        MemoryWriterOps.TryParse(reply, max: 10, out var ops, out var dropped).Should().BeTrue();

        ops.Should().ContainSingle().Which.Quote.Should().Be("I'll be there");
        dropped.Select(d => (d.Index, d.Reason)).Should().Equal(
            (1, MemoryWriterOps.DropReason.UnknownAction),
            (2, MemoryWriterOps.DropReason.MissingText),
            (3, MemoryWriterOps.DropReason.MissingId),
            (4, MemoryWriterOps.DropReason.NotAnObject));
    }

    [Fact]
    public void EntriesPastTheLimitAreRecordedAsOverLimit()
    {
        const string reply = """{"ops": [{"op": "add", "text": "a | b | c"}, {"op": "add", "text": "d | e | f"}]}""";

        MemoryWriterOps.TryParse(reply, max: 1, out var ops, out var dropped).Should().BeTrue();

        ops.Should().ContainSingle();
        dropped.Should().ContainSingle().Which.Reason.Should().Be(MemoryWriterOps.DropReason.OverLimit);
    }

    [Fact]
    public void AQuotesOffsetsGoToTheMemorysMetadataOnlyWhenThereAreSome()
    {
        var metadata = new Dictionary<string, object> { ["trust"] = "x" };

        SourceQuoteMetadata.With(metadata, 4, 12).Should().Contain(new KeyValuePair<string, object>("source_quote_start", 4))
            .And.Contain(new KeyValuePair<string, object>("source_quote_end", 12));
        SourceQuoteMetadata.With(metadata, null, null).Should().BeSameAs(metadata);
    }
}
