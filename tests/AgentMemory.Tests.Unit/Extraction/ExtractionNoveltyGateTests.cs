using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Not paying for an extraction call on "ok, thanks!" (E4) — and, far more importantly, paying for it
/// whenever there is the slightest chance the turn said something.
/// </summary>
/// <remarks>
/// <para>
/// <b>The asymmetry is worse here than anywhere else in the system, so the tests are lopsided.</b>
/// Declining to gate costs one extraction call. Gating a turn that did carry a fact means the memory
/// is never formed — nothing downstream can recover it, and nothing can even report it missing. The
/// bulk of these cases are therefore phrases that must still be extracted.
/// </para>
/// </remarks>
public sealed class ExtractionNoveltyGateTests
{
    private static Message M(string content, string role = "user") => new()
    {
        MessageId = $"m-{content.GetHashCode():X}",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = role,
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    private static bool Worth(params string[] contents) =>
        ExtractionNoveltyGate.IsWorthExtracting(contents.Select(c => M(c)).ToList());

    // ── must still be extracted ───────────────────────────────────────────

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("sure")]
    [InlineData("nope")]
    [InlineData("correct")]
    [InlineData("right")]
    public void ABareAnswerIsContentful(string answer)
    {
        // THE dangerous class. Each of these is a complete answer to a question -- "do you eat meat?"
        // "no" -- and the question may have been in the PREVIOUS batch, so nothing in this one reveals
        // that a fact was just asserted. They are kept out of the vocabulary deliberately.
        Worth(answer).Should().BeTrue();
    }

    [Theory]
    [InlineData("Basel")]
    [InlineData("tea")]
    [InlineData("3")]
    [InlineData("42 Rue Lafayette")]
    public void AShortContentfulTurnIsNotAPleasantry(string content)
    {
        // Short is not the same as empty. A one-word answer naming a place, a preference or a quantity
        // is exactly the fact worth remembering.
        Worth(content).Should().BeTrue();
    }

    [Fact]
    public void AQuestionMarkAlwaysWinsExtraction()
    {
        // Someone asked something, so an answer is in play -- possibly a single word in this very
        // batch. Cheap insurance against the whole ambiguous class.
        Worth("ok thanks?").Should().BeTrue();
    }

    [Fact]
    public void OnePleasantryBesideOneRealTurnStillExtracts()
    {
        // The batch is the unit. One contentful message makes the whole call worth paying for.
        Worth("thanks!", "I moved to Zurich last month").Should().BeTrue();
    }

    [Fact]
    public void AnEmptyBatchIsNotGated()
    {
        // Biased toward extracting even here: an empty batch is cheap, and what to do with it belongs
        // to the extractors rather than to a gate guessing on their behalf.
        ExtractionNoveltyGate.IsWorthExtracting([]).Should().BeTrue();
    }

    [Fact]
    public void AGratitudeWordInsideARealSentenceDoesNotGate()
    {
        // "thanks" appears constantly inside contentful turns. The whole message must be explained by
        // the vocabulary, token by token -- not merely contain one of its words.
        Worth("thanks, my address is 12 Bahnhofstrasse").Should().BeTrue();
    }

    // ── may be skipped ────────────────────────────────────────────────────

    [Theory]
    [InlineData("ok")]
    [InlineData("Thanks!")]
    [InlineData("thank you so much")]
    [InlineData("ok, got it")]
    [InlineData("great, thanks")]
    [InlineData("hi there")]
    [InlineData("good morning")]
    [InlineData("you're welcome")]
    [InlineData("perfect")]
    [InlineData("lol")]
    public void PureAcknowledgementIsNotWorthACompletion(string content) =>
        Worth(content).Should().BeFalse();

    [Theory]
    [InlineData("no problem")]
    [InlineData("no worries")]
    public void APleasantryStartingWithAnAmbiguousTokenIsNotSkipped(string content)
    {
        // A missed skip, and the correct trade. "no problem" is plainly a pleasantry, but gating it
        // means putting "no" in the vocabulary -- and then a bare "no", the complete answer to "do you
        // eat meat?", stops being extracted. One wasted call is the cheaper mistake by a wide margin.
        Worth(content).Should().BeTrue();
    }

    [Fact]
    public void AWholeBatchOfPleasantriesIsSkipped() =>
        Worth("thanks!", "You're welcome!").Should().BeFalse();

    [Fact]
    public void BlankMessagesDoNotByThemselvesForceExtraction() =>
        Worth("ok", "   ").Should().BeFalse();

    [Fact]
    public void PunctuationAndCasingDoNotDefeatTheVocabulary() =>
        Worth("OK... Thanks!!!").Should().BeFalse();

    // ── shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheGateIsOffByDefault()
    {
        // It changes what gets extracted, and transcript bytes are fingerprinted into every measured
        // run in this track.
        new ExtractionOptions().SkipUninformativeTurns.Should().BeFalse();
    }
}
