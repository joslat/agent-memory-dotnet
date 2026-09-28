using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.Core.Services;
using AgentMemory.Extraction.Llm;
using AgentMemory.Extraction.Llm.Internal;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 36.1: what memory knows about <i>when</i> reaches the prompt, at the precision it was stated. Measured before:
/// "moved to Lyon in March 2024" was stored (<c>valid_from 2024-03-01</c>) and rendered as <c>Nadia moved to Lyon</c>,
/// so "When did I move to Lyon?" was answered "I don't have the date"; rendering the stored instant as a day
/// would have answered "on 1 March", a day nobody said.
/// </summary>
public sealed class DatesInContextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    // ── The parser knows the precision it read ───────────────────────────────────────────────────

    [Theory]
    [InlineData("2024", DatePrecision.Year)]
    [InlineData("2024-03", DatePrecision.Month)]
    [InlineData("2024-03-12", DatePrecision.Day)]
    [InlineData("2024-03-12T17:00:00Z", DatePrecision.Instant)]
    [InlineData("March 12, 2024", DatePrecision.Unspecified)]
    public void The_parser_keeps_how_precisely_a_date_was_written(string raw, DatePrecision expected)
    {
        PeriodDateConverter.ParseWithPrecision(raw, PeriodEdge.Start)!.Value.Precision.Should().Be(expected);
    }

    [Fact]
    public void An_end_keeps_its_precision_and_its_last_instant()
    {
        var parsed = PeriodDateConverter.ParseWithPrecision("2027-06", PeriodEdge.End)!.Value;

        parsed.Precision.Should().Be(DatePrecision.Month);
        parsed.At.Should().Be(new DateTimeOffset(2027, 7, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1));
    }

    [Fact]
    public async Task The_extractor_hands_the_precision_to_the_fact()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"entities":[],"facts":[{"subject":"user","predicate":"moved to","object":"Lyon","confidence":0.9,"valid_from":"2024-03","valid_until":"2027"}],"preferences":[],"relations":[]}"""))));
        var sut = new LlmUnifiedMemoryExtractor(client,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true }), NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        var result = await sut.ExtractAsync([new Message
        {
            MessageId = "m1", ConversationId = "c", SessionId = "s", Role = "user",
            Content = "I moved to Lyon in March 2024.", TimestampUtc = T0,
        }]);

        var fact = result.Facts.Should().ContainSingle().Subject;
        fact.ValidFrom.Should().Be(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero));
        fact.ValidFromPrecision.Should().Be(DatePrecision.Month);
        fact.ValidUntilPrecision.Should().Be(DatePrecision.Year);
    }

    // ── One rule for how dates read ──────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset March2024 = new(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndJune2027 = new DateTimeOffset(2027, 7, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1);

    [Fact]
    public void Dates_read_at_their_stated_precision()
    {
        FactDates.Suffix(March2024, DatePrecision.Month, null, DatePrecision.Unspecified).Should().Be(" (since 2024-03)");
        FactDates.Suffix(null, DatePrecision.Unspecified, EndJune2027, DatePrecision.Month).Should().Be(" (until 2027-06)");
        FactDates.Suffix(March2024, DatePrecision.Year, EndJune2027, DatePrecision.Year).Should().Be(" (2024 to 2027)");
        FactDates.Suffix(null, DatePrecision.Unspecified, null, DatePrecision.Unspecified).Should().BeEmpty();
    }

    [Fact]
    public void An_event_that_starts_and_ends_on_one_day_reads_as_that_day()
    {
        var day = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

        FactDates.Suffix(day, DatePrecision.Day, day.AddDays(1).AddTicks(-1), DatePrecision.Day).Should().Be(" (on 2026-09-26)");
    }

    /// <summary>
    /// 36.1 (run 5): "yesterday I went hiking" was stored as valid_from alone and read "went hiking in Sintra (since
    /// 2026-09-26)". An event reads as the day it happened, at its precision, whatever validity it also carries.
    /// </summary>
    [Fact]
    public void An_event_reads_as_the_day_it_happened()
    {
        var day = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        var hike = new Fact
        {
            FactId = "h", Subject = "Lucas", Predicate = "went hiking in", Object = "Sintra", Confidence = 0.9, CreatedAtUtc = day,
            OccurredOn = day, OccurredOnPrecision = DatePrecision.Day, ValidFrom = day, ValidFromPrecision = DatePrecision.Day,
        };

        FactDates.Suffix(hike).Should().Be(" (on 2026-09-26)");
        FactDates.Suffix(hike with { OccurredOnPrecision = DatePrecision.Month }).Should().Be(" (on 2026-09)");
        FactDates.Suffix(hike with { OccurredOn = null }).Should().Be(" (since 2026-09-26)", "a state is unchanged");
    }

    [Fact]
    public async Task The_extractor_hands_the_event_day_to_the_fact()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"entities":[],"facts":[{"subject":"user","predicate":"went hiking in","object":"Sintra","confidence":0.9,"occurred_on":"2026-09"}],"preferences":[],"relations":[]}"""))));
        var sut = new LlmUnifiedMemoryExtractor(client,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true }), NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        var result = await sut.ExtractAsync([new Message
        {
            MessageId = "m1", ConversationId = "c", SessionId = "s", Role = "user",
            Content = "I went hiking in Sintra this month.", TimestampUtc = T0,
        }]);

        var fact = result.Facts.Should().ContainSingle().Subject;
        fact.OccurredOn.Should().Be(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        fact.OccurredOnPrecision.Should().Be(DatePrecision.Month);
        fact.ValidFrom.Should().BeNull();
    }

    [Fact]
    public void An_unrecorded_precision_reads_as_a_day_as_it_always_has()
    {
        FactDates.Format(March2024, DatePrecision.Unspecified).Should().Be("2024-03-01");
    }

    // ── Both renderers, on and off ───────────────────────────────────────────────────────────────

    private static MemoryContext Context() => new()
    {
        SessionId = "today",
        AssembledAtUtc = T0,
        RelevantFacts = new MemoryContextSection<Fact>
        {
            Items =
            [
                new Fact
                {
                    FactId = "f1", Subject = "Nadia", Predicate = "moved to", Object = "Lyon", Confidence = 1,
                    CreatedAtUtc = T0, ValidFrom = March2024, ValidFromPrecision = DatePrecision.Month,
                },
            ],
        },
        RelevantMessages = new MemoryContextSection<Message>
        {
            Items =
            [
                new Message
                {
                    MessageId = "m-old", ConversationId = "c", SessionId = "last-week", Role = "user",
                    Content = "I went hiking yesterday.", TimestampUtc = new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero),
                },
                new Message
                {
                    MessageId = "m-now", ConversationId = "c", SessionId = "today", Role = "user",
                    Content = "Hello again.", TimestampUtc = T0,
                },
            ],
        },
    };

    [Fact]
    public void The_agent_framework_renderer_shows_the_date_when_asked()
    {
        var on = MafTypeMapper.ToContextMessages(Context(), new ContextFormatOptions { IncludeDates = true }).Select(m => m.Text).ToList();
        var off = MafTypeMapper.ToContextMessages(Context(), new ContextFormatOptions()).Select(m => m.Text).ToList();

        on.Should().Contain(t => t.Contains("Nadia moved to Lyon (since 2024-03)"));
        on.Should().Contain(t => t == "[2026-09-20] I went hiking yesterday.", "a turn from another session says which day it was");
        on.Should().Contain(t => t == "Hello again.", "this session's own turns need no date");
        off.Should().NotContain(t => t.Contains("since 2024-03")).And.Contain(t => t == "I went hiking yesterday.");
    }

    [Fact]
    public void The_core_formatter_shows_the_date_when_asked()
    {
        var result = new RecallResult { Context = Context(), TotalItemsRetrieved = 3 };

        var on = MemoryContextFormatter.FormatRecallResult(result, new MemoryContextFormatterOptions { IncludeDates = true });
        var off = MemoryContextFormatter.FormatRecallResult(result);

        on.Should().Contain("- Nadia moved to Lyon (since 2024-03)")
            .And.Contain("[2026-09-20] [user]: I went hiking yesterday.")
            .And.Contain("\n[user]: Hello again.");
        off.Should().NotContain("since 2024-03").And.NotContain("[2026-09-20]");
    }

    [Fact]
    public void A_due_reminder_prints_its_date_at_the_stated_precision_on_both_renderers()
    {
        var due = new Fact
        {
            FactId = "d", Subject = "passport", Predicate = "expires", Object = "soon", Confidence = 1, CreatedAtUtc = T0,
            ValidFrom = March2024, ValidFromPrecision = DatePrecision.Month,
        };
        var context = new MemoryContext { SessionId = "s", AssembledAtUtc = T0, DueFacts = new MemoryContextSection<Fact> { Items = [due] } };

        MafTypeMapper.ToContextMessages(context, new ContextFormatOptions()).Select(m => m.Text)
            .Should().Contain(t => t.Contains("(valid from 2024-03)"));
        MemoryContextFormatter.FormatRecallResult(new RecallResult { Context = context, TotalItemsRetrieved = 1 })
            .Should().Contain("(valid from 2024-03)");
    }

    // ── The extractor is told when each turn was said ────────────────────────────────────────────

    /// <summary>
    /// 36.1: the temporal instruction promises each turn its time, and the single-session extractors sent none, so
    /// "a half marathon in April", said in September 2026, was stored as April 2025 (the model's reasoning: "turns have
    /// no prefix times").
    /// </summary>
    [Theory]
    [InlineData(TemporalValidityMode.Extract, true)]
    [InlineData(TemporalValidityMode.Ignore, false)]
    public async Task The_dated_extractors_send_each_turns_time(TemporalValidityMode mode, bool stamped)
    {
        var captured = new List<IEnumerable<ChatMessage>>();
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(captured.Add), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"entities":[],"facts":[],"preferences":[],"relations":[]}"""))));
        var options = Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, TemporalValidity = mode });
        var message = new Message
        {
            MessageId = "m1", ConversationId = "c", SessionId = "s", Role = "user",
            Content = "I'm training for a half marathon in April.", TimestampUtc = T0,
        };

        await new LlmUnifiedMemoryExtractor(client, options, NullLogger<LlmUnifiedMemoryExtractor>.Instance).ExtractAsync([message]);
        await new LlmFactExtractor(client, options, NullLogger<LlmFactExtractor>.Instance).ExtractAsync([message]);

        var stamp = "[2026-09-27T10:00:00.0000000+00:00] user: I'm training";
        captured.Should().HaveCount(2).And.OnlyContain(call =>
            call.Any(m => m.Role == ChatRole.User && m.Text.Contains(stamp, StringComparison.Ordinal)) == stamped);
    }

    /// <summary>
    /// 36.1: the model wrote "2024-03-01" for "in March 2024", so the stored precision was a day and the agent
    /// answered "on March 1st" (run 3). The instruction asks for the stated precision, which the parser keeps.
    /// </summary>
    [Fact]
    public void The_temporal_instruction_asks_for_the_stated_precision()
    {
        ExtractionPromptSemantics.TemporalValidityInstruction(TemporalValidityMode.Extract)
            .Should().Contain("only as precisely as it was stated").And.Contain("\"2024-03\" for \"in March 2024\"")
            .And.Contain("\"occurred_on\"", "36.1: a one-off event gets the day it happened, not a period");
        ExtractionPromptSemantics.TemporalValidityInstruction(TemporalValidityMode.Ignore).Should().BeEmpty();
    }

    /// <summary>36.1 review: the conflict and supersession blocks print a date at its precision too.</summary>
    [Fact]
    public void The_projection_blocks_print_dates_at_their_precision()
    {
        var fact = new Fact
        {
            FactId = "f", Subject = "Nadia", Predicate = "lives in", Object = "Lyon", Confidence = 1, CreatedAtUtc = T0,
            ValidFrom = March2024, ValidFromPrecision = DatePrecision.Month,
        };
        AgentMemory.Core.Services.Projection.ConflictProjectionFeature.Describe(fact).Should().Be("Lyon (2024-03)");
        AgentMemory.Core.Services.Projection.ConflictProjectionFeature.Describe(fact with
            {
                OccurredOn = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero), OccurredOnPrecision = DatePrecision.Day,
            })
            .Should().Be("Lyon (2026-09-26)", "an event reads as the day it happened");

        var chain = new[] { new SupersededFact("Hamburg", T0, EndJune2027) { ValidUntilPrecision = DatePrecision.Month } };
        AgentMemory.Core.Services.Projection.SupersessionProjectionFeature.Render(chain).Should().Contain("since 2027-06;");
    }

    /// <summary>36.3 review: the fact extractor is told that someone else's taste is a fact about them.</summary>
    [Fact]
    public void The_fact_extractor_carries_the_own_preferences_instruction()
    {
        LlmFactExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch,
                ownPreferencesOnly: true)
            .Should().Contain("A preference is the user's own taste");
    }
}
