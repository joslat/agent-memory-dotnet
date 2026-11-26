using AgentMemory.Extraction.Llm;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// RVW-REST-WRITER (the review's writer-3): an operation dropped because its quote is not in the message is fixed only
/// by the same operation with a quote that is. Sent back with an empty quote (or none), it was accepted by the
/// same-text rule and the unsupported memory was written.
/// </summary>
public sealed class MemoryWriterQuoteCorrectionTests
{
    private static readonly MemoryWriterContract.Issue NotInTheMessage = new(
        MemoryWriterOps.DropReason.QuoteNotFound, 0, "fact", "user | lives in | Paris", "its quote is not exact, contiguous words of the last message");

    [Fact]
    public void A_quote_that_is_not_found_is_not_fixed_by_dropping_the_quote()
    {
        var resent = new MemoryWriterOps.Op("add", "fact", "user | lives in | Paris", null) { Index = 0 };

        var (ops, unfixed) = MemoryWriterContract.Merge([], [NotInTheMessage], [resent], [], 10);

        ops.Should().BeEmpty("nothing in the message supports it");
        unfixed.Should().ContainSingle().Which.Reason.Should().Be(MemoryWriterOps.DropReason.QuoteNotFound);
    }

    [Fact]
    public void A_quote_that_is_found_fixes_it()
    {
        var resent = new MemoryWriterOps.Op("add", "fact", "user | lives in | Paris", null) { Index = 0, Quote = "moved to Paris", QuoteStart = 15, QuoteEnd = 29 };

        var (ops, unfixed) = MemoryWriterContract.Merge([], [NotInTheMessage], [resent], [], 10);

        ops.Should().ContainSingle().Which.Quote.Should().Be("moved to Paris");
        unfixed.Should().BeEmpty();
    }

    // RVW-REST-WRITER (the review's writer-6): a replacement that names no shown memory of its kind is written as an
    // addition and the old memory stays live; the writer now names such replacements (a warning and a span tag).
    [Fact]
    public void A_replacement_whose_target_was_not_shown_is_named()
    {
        IReadOnlyList<MemoryWriterOps.Stored> shown = [new("fact-1", 'F', "user | works at | Contoso") { Label = "F1" },
            new("pref-1", 'P', "likes tea") { Label = "P1" }];
        IReadOnlyList<MemoryWriterOps.Op> ops =
        [
            new("replace", "fact", "user | works at | Fabrikam", "F1"),            // found: fine
            new("replace", "fact", "user | works at | Fabrikam", "F12"),           // never shown
            new("replace", "preference", "likes coffee", "F1"),                    // shown, but a fact
            new("correct", "fact", "user | works at | Fabrikam", "fact-1"),        // the stored id, not its label
            new("add", "fact", "user | lives in | Madrid", null),
        ];

        MemoryWriterOps.UnresolvedReplacements(ops, shown).Select(o => o.Id).Should().Equal("F12", "F1", "fact-1");
    }

    // WRITER-QUOTES-REQUIRED (off by default, measured before any default changes): with quotes required, a write without a
    // quote is dropped as unsupported and a confirm's quote must be in the message; off, both are kept as before.
    [Fact]
    public void Required_quotes_drop_a_write_without_one_and_check_a_confirms()
    {
        const string message = "Ana moved to Lyon in March.";
        IReadOnlyList<MemoryWriterOps.Op> answered =
        [
            new("add", "fact", "Ana | lives in | Lyon", null) { Index = 0, Quote = "Ana moved to Lyon" },
            new("add", "fact", "Ana | works at | Contoso", null) { Index = 1 },
            new("confirm", "fact", "", "F1") { Index = 2, Quote = "words nobody said" },
            new("confirm", "fact", "", "F2") { Index = 3 },
        ];

        var (keptOff, droppedOff) = LlmMemoryWriter.Locate(answered, [], message);
        keptOff.Select(o => o.Index).Should().Equal(0, 1, 2, 3);
        droppedOff.Should().BeEmpty();

        var (kept, dropped) = LlmMemoryWriter.Locate(answered, [], message, requireQuotes: true);
        kept.Select(o => o.Index).Should().Equal(0, 3);
        dropped.Select(d => (d.Index, d.Reason)).Should().Equal((1, MemoryWriterOps.DropReason.QuoteMissing), (2, MemoryWriterOps.DropReason.QuoteNotFound));
    }
}
