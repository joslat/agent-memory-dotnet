using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Extracting with earlier turns as context (E2) — and never extracting <i>from</i> them.
/// </summary>
/// <remarks>
/// <para>
/// One batch is often not enough to understand itself: "I moved there last year" needs the turn that
/// named the place. Widening the batch fixes the reference and breaks something worse.
/// </para>
/// <para>
/// <b>Context must not be a target, and that stopped being a token-efficiency question this week.</b>
/// Re-extracting the preceding turns re-asserts stored facts. That used to be merely wasteful — the
/// exact triple MERGEs — but a re-assertion now earns confidence (S2) and increments the
/// <c>mention_count</c> the salience reranker reads (R7). Extracting context would make a fact gain
/// both every time it sat inside a sliding window, so two signals that mean "the world keeps saying
/// this" would quietly come to mean "this was said recently". Nothing would look broken.
/// </para>
/// </remarks>
public sealed class ExtractionWindowTests
{
    private static Message M(string id, string content, string role = "user") => new()
    {
        MessageId = id,
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = role,
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    // ── rendering ─────────────────────────────────────────────────────────

    [Fact]
    public void WithoutContextTheTranscriptIsByteIdentical()
    {
        // The off state. Every sealed measurement in this track was taken on this rendering, and
        // transcript bytes are fingerprinted into each of them.
        var targets = new[] { M("m-1", "I live in Zurich"), M("m-2", "Noted.", "assistant") };

        ConversationTextBuilder.BuildWindow(ExtractionWindow.ForTargets(targets), numbered: false)
            .Should().Be(ConversationTextBuilder.Build(targets));
        ConversationTextBuilder.BuildWindow(ExtractionWindow.ForTargets(targets), numbered: true)
            .Should().Be(ConversationTextBuilder.BuildNumbered(targets));
    }

    [Fact]
    public void ContextIsFencedAheadOfTheTargets()
    {
        var rendered = ConversationTextBuilder.BuildWindow(
            new ExtractionWindow { Targets = [M("m-2", "I moved there last year")], Context = [M("m-1", "Zurich is lovely")] },
            numbered: false);

        rendered.Should().Contain(ConversationTextBuilder.ContextOpen)
            .And.Contain("Zurich is lovely")
            .And.Contain(ConversationTextBuilder.ContextClose);
        rendered.IndexOf(ConversationTextBuilder.ContextClose, StringComparison.Ordinal)
            .Should().BeLessThan(rendered.IndexOf("I moved there last year", StringComparison.Ordinal),
                "the fence has to close before the turns the model may extract from");
    }

    [Fact]
    public void ContextIsNeverNumbered()
    {
        // THE provenance trap. L3c numbers turns from 1 and resolves turn N positionally to
        // Targets[N-1]. If context joined the numbering, every target index would shift by the context
        // length -- and the result is not a crash, it is each fact attributed to a turn a few places
        // earlier, which afterwards is indistinguishable from precise attribution.
        var rendered = ConversationTextBuilder.BuildWindow(
            new ExtractionWindow
            {
                Targets = [M("m-3", "the third")],
                Context = [M("m-1", "the first"), M("m-2", "the second")],
            },
            numbered: true);

        rendered.Should().Contain("[1] user: the third");
        rendered.Should().NotContain("[1] user: the first");
        rendered.Should().NotContain("[2]");
    }

    // ── the contract ──────────────────────────────────────────────────────

    [Fact]
    public void AWindowWithNoContextIsTheDefault() =>
        ExtractionWindow.ForTargets([M("m-1", "hi")]).HasContext.Should().BeFalse();

    [Fact]
    public void TheOptionIsOffByDefault() =>
        new ExtractionOptions().ExtractionContextTurns.Should().Be(0);

    [Fact]
    public async Task ContextIsKeptOutOfProvenance()
    {
        // The invariant the whole design exists to protect. An EXTRACTED_FROM edge to a context turn
        // would assert the memory was stated in the very turn the extractor was told not to extract
        // from.
        var stage = new ExtractionStage(
            [], [], [], [], [], Substitute.For<IEntityResolver>(),
            Options.Create(new ExtractionOptions()),
            NullLogger<ExtractionStage>.Instance);

        var result = await stage.ExtractWithContextAsync(
            new ExtractionWindow
            {
                Targets = [M("m-2", "and I moved there")],
                Context = [M("m-1", "Zurich is lovely")],
            },
            ExtractionTypes.All);

        result.SourceMessageIds.Should().Equal(["m-2"]);
    }

    [Fact]
    public async Task AnExtractorThatIgnoresContextStillSeesOnlyItsTargets()
    {
        // The default interface method, exercised on a REAL implementation rather than a substitute:
        // a mocking proxy intercepts the default body too, so asserting through one would only prove
        // the mock forwards what it was told to.
        //
        // An extractor written before E2 must keep working, and must receive exactly the turns it
        // always received -- not a widened batch it would extract from, which is the failure the whole
        // Targets/Context split exists to prevent.
        // Through the INTERFACE, not the class: a default interface method is not inherited into the
        // implementing type, so this is also how a caller must reach it in production.
        IFactExtractor extractor = new LegacyFactExtractor();

        await extractor.ExtractWithContextAsync(
            new ExtractionWindow { Targets = [M("m-2", "target")], Context = [M("m-1", "context")] });

        ((LegacyFactExtractor)extractor).Seen.Should().ContainSingle()
            .Which.MessageId.Should().Be("m-2");
    }

    /// <summary>An extractor written before E2 existed: it implements only the original overload.</summary>
    private sealed class LegacyFactExtractor : IFactExtractor
    {
        public List<Message> Seen { get; } = [];

        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            return Task.FromResult<IReadOnlyList<ExtractedFact>>([]);
        }
    }
}
