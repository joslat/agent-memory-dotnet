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
/// A field a rung <b>asks</b> for must survive that rung's projection into the domain record.
/// </summary>
/// <remarks>
/// <para>
/// The existing conformance tests check that every rung's <i>prompt</i> carries a setting's
/// instruction. That is only half the contract, and the missing half had already failed silently: the
/// multi-session batch rung asked for <c>valid_from</c>/<c>valid_until</c> whenever
/// <c>TemporalValidityMode.Extract</c> was set — the instruction is shared, so it could not not ask —
/// and then dropped both fields on the floor when building its <see cref="ExtractedFact"/>. The
/// setting was a no-op under batched extraction, and the prompt-level test passed the whole time.
/// </para>
/// <para>
/// This is the same "a setting only some extractors respect" defect the shared-semantics type was
/// created to prevent, arriving one layer lower than anyone was looking. So the rule is asserted where
/// it can actually be broken: parse a response that populates the field, and require the value to come
/// out the other end.
/// </para>
/// </remarks>
public sealed class ExtractorProjectionConformanceTests
{
    private static readonly DateTimeOffset ValidFrom = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidUntil = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static IChatClient ClientReturning(string payload)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, payload))));
        return client;
    }

    private static IReadOnlyList<Message> OneMessage() =>
    [
        new Message
        {
            MessageId = "m-1",
            ConversationId = "c-1",
            SessionId = "s-1",
            Role = "user",
            Content = "I am on the Zurich project until September.",
            TimestampUtc = ValidFrom,
        },
    ];

    private const string FactFields =
        "\"subject\":\"user\",\"predicate\":\"works on\",\"object\":\"Zurich project\",\"confidence\":0.9," +
        "\"valid_from\":\"2026-03-01T00:00:00+00:00\",\"valid_until\":\"2026-09-01T00:00:00+00:00\"," +
        "\"source_role\":\"assistant\",\"source_turn\":2";

    private const string PreferenceFields =
        "\"category\":\"travel\",\"preference\":\"aisle seats\",\"confidence\":0.9," +
        "\"source_role\":\"assistant\",\"source_turn\":2";

    // ── the unified rung ──────────────────────────────────────────────────

    [Fact]
    public async Task TheUnifiedRungCarriesEveryRequestedFieldThrough()
    {
        var payload =
            $"{{\"entities\":[],\"facts\":[{{{FactFields}}}]," +
            $"\"preferences\":[{{{PreferenceFields}}}],\"relations\":[]}}";
        var sut = new LlmUnifiedMemoryExtractor(
            ClientReturning(payload),
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, MaxRetries = 0 }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        var result = await sut.ExtractAsync(OneMessage());

        var fact = result.Facts.Should().ContainSingle().Subject;
        fact.ValidFrom.Should().Be(ValidFrom);
        fact.ValidUntil.Should().Be(ValidUntil);
        fact.SourceRole.Should().Be("assistant");
        fact.SourceTurn.Should().Be(2);
        var preference = result.Preferences.Should().ContainSingle().Subject;
        preference.SourceRole.Should().Be("assistant");
        preference.SourceTurn.Should().Be(2);
    }

    // ── the multi-session batch rung — where the defect was ───────────────

    [Fact]
    public async Task TheBatchRungCarriesEveryRequestedFieldThrough()
    {
        // The regression that shipped: this rung emitted the temporal instruction and then discarded
        // the answer, so TemporalValidityMode.Extract was silently inert under batched extraction.
        var alias = LlmMultiSessionExtractionResponseContract.Alias(0);
        var payload =
            $"{{\"processed_source_sessions\":[\"{alias}\"],\"entities\":[]," +
            $"\"facts\":[{{\"source_session\":\"{alias}\",{FactFields}}}]," +
            $"\"preferences\":[{{\"source_session\":\"{alias}\",{PreferenceFields}}}],\"relations\":[]}}";

        var options = Options.Create(new LlmExtractionOptions
        {
            UseUnifiedExtraction = true,
            UseMultiSessionBatchExtraction = true,
            MaxRetries = 0,
        });
        var sut = new LlmMultiSessionUnifiedMemoryExtractor(
            ClientReturning(payload),
            options,
            NullLogger<LlmMultiSessionUnifiedMemoryExtractor>.Instance,
            new LlmExtractionBatchConcurrencyLimiter(options));

        var results = await sut.ExtractAsync(
            [new ExtractionRequest { SessionId = "s-1", UserId = "owner-1", Messages = OneMessage() }],
            maxSessionsPerBatch: 1,
            maxInputTokens: 100_000);

        var extracted = results["s-1"];
        var fact = extracted.Facts.Should().ContainSingle().Subject;
        fact.ValidFrom.Should().Be(ValidFrom,
            "this rung asks for valid_from whenever Extract is set, so dropping it makes the setting a no-op");
        fact.ValidUntil.Should().Be(ValidUntil);
        fact.SourceRole.Should().Be("assistant");
        fact.SourceTurn.Should().Be(2);
        var preference = extracted.Preferences.Should().ContainSingle().Subject;
        preference.SourceRole.Should().Be("assistant");
        preference.SourceTurn.Should().Be(2);
    }

    // ── the per-kind rungs ────────────────────────────────────────────────

    [Fact]
    public async Task ThePerKindFactRungCarriesEveryRequestedFieldThrough()
    {
        var sut = new LlmFactExtractor(
            ClientReturning($"{{\"facts\":[{{{FactFields}}}]}}"),
            Options.Create(new LlmExtractionOptions { MaxRetries = 0 }),
            NullLogger<LlmFactExtractor>.Instance);

        var facts = await sut.ExtractAsync(OneMessage());

        var fact = facts.Should().ContainSingle().Subject;
        fact.ValidFrom.Should().Be(ValidFrom);
        fact.ValidUntil.Should().Be(ValidUntil);
        fact.SourceRole.Should().Be("assistant");
        fact.SourceTurn.Should().Be(2);
    }

    [Fact]
    public async Task ThePerKindPreferenceRungCarriesEveryRequestedFieldThrough()
    {
        var sut = new LlmPreferenceExtractor(
            ClientReturning($"{{\"preferences\":[{{{PreferenceFields}}}]}}"),
            Options.Create(new LlmExtractionOptions { MaxRetries = 0 }),
            NullLogger<LlmPreferenceExtractor>.Instance);

        var preferences = await sut.ExtractAsync(OneMessage());

        var preference = preferences.Should().ContainSingle().Subject;
        preference.SourceRole.Should().Be("assistant");
        preference.SourceTurn.Should().Be(2);
    }

    // ── and the absence direction ─────────────────────────────────────────

    [Fact]
    public async Task AResponseThatOmitsTheFieldsProducesNullsRatherThanDefaults()
    {
        // Null is the meaningful value on all three: an invented valid_until silently removes a memory
        // from every future answer, and an invented source_role moves a trust stamp. A projection that
        // substituted "now" or "user" for a missing field would be worse than one that dropped it.
        var payload =
            "{\"entities\":[],\"facts\":[{\"subject\":\"user\",\"predicate\":\"likes\"," +
            "\"object\":\"tea\",\"confidence\":0.9}],\"preferences\":[],\"relations\":[]}";
        var sut = new LlmUnifiedMemoryExtractor(
            ClientReturning(payload),
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, MaxRetries = 0 }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        var fact = (await sut.ExtractAsync(OneMessage())).Facts.Should().ContainSingle().Subject;

        fact.ValidFrom.Should().BeNull();
        fact.ValidUntil.Should().BeNull();
        fact.SourceRole.Should().BeNull();
        fact.SourceTurn.Should().BeNull();
    }
}
