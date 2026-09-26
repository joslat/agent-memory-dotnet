using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using AgentMemory.Extraction.Llm.Internal;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Validity dates as extraction models actually write them.
/// </summary>
/// <remarks>
/// <para>
/// Captured live: asked for "ISO-8601", a model that knows only the month writes
/// <c>"valid_from": "2026-08"</c> — valid ISO-8601 (a reduced-precision calendar month), but outside
/// the extended profile System.Text.Json accepts. The typed parse threw, the runner reported it as
/// "not valid JSON", the model repeated the same date on every re-prompt, and the extractor discarded
/// every entity, fact and preference in the response. These tests fail on the pre-fix DTO.
/// </para>
/// <para>
/// The second defect: a date-only value the native parser DID accept was read at the machine's local
/// midnight, so the same extraction stored different instants in different time zones.
/// </para>
/// </remarks>
public sealed class PeriodDateConverterTests
{
    private static readonly DateTimeOffset Aug1 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- Start of period (valid_from) ----

    [Theory]
    [InlineData("2026", 2026, 1, 1)]
    [InlineData("2026-08", 2026, 8, 1)]
    [InlineData("2026-8", 2026, 8, 1)]
    [InlineData("2026-08-15", 2026, 8, 15)]
    [InlineData("2026-8-5", 2026, 8, 5)]
    [InlineData(" 2026-08 ", 2026, 8, 1)]
    public void Start_edge_reads_every_calendar_precision_as_the_first_instant_in_UTC(
        string raw, int y, int m, int d)
    {
        PeriodDateConverter.Parse(raw, PeriodEdge.Start)
            .Should().Be(new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero));
    }

    // ---- End of period (valid_until) ----

    [Theory]
    [InlineData("2026", 2026, 12, 31)]
    [InlineData("2026-08", 2026, 8, 31)]
    [InlineData("2026-02", 2026, 2, 28)]
    [InlineData("2028-02", 2028, 2, 29)]
    [InlineData("2026-08-15", 2026, 8, 15)]
    public void End_edge_reads_every_calendar_precision_as_the_last_instant_of_the_period(
        string raw, int y, int m, int d)
    {
        var expected = new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero).AddDays(1).AddTicks(-1);
        PeriodDateConverter.Parse(raw, PeriodEdge.End).Should().Be(expected);
    }

    [Fact]
    public void End_of_year_9999_does_not_overflow()
    {
        PeriodDateConverter.Parse("9999", PeriodEdge.End).Should().Be(DateTimeOffset.MaxValue);
    }

    // ---- Timestamps and time zones ----

    [Fact]
    public void A_date_only_value_is_UTC_not_the_machines_local_midnight()
    {
        // The native parser gave 2026-08-01T00:00+<local offset>: a different instant per machine.
        var parsed = PeriodDateConverter.Parse("2026-08-01", PeriodEdge.Start);
        parsed.Should().Be(Aug1);
        parsed!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_timestamp_without_offset_is_UTC()
    {
        PeriodDateConverter.Parse("2026-08-01T10:30:00", PeriodEdge.Start)
            .Should().Be(new DateTimeOffset(2026, 8, 1, 10, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_timestamp_with_offset_keeps_its_instant_and_is_normalized_to_UTC()
    {
        var parsed = PeriodDateConverter.Parse("2026-08-01T10:00:00+02:00", PeriodEdge.End);
        parsed.Should().Be(new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero));
        parsed!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    // ---- Free text ----

    [Fact]
    public void Free_text_dates_give_a_start_but_never_an_end_without_a_time()
    {
        // "August 2026" and "1 August 2026" parse to the same instant; as an END that would expire a
        // month-long fact on its first day. Unbounded is the safe reading.
        PeriodDateConverter.Parse("August 2026", PeriodEdge.Start).Should().Be(Aug1);
        PeriodDateConverter.Parse("August 2026", PeriodEdge.End).Should().BeNull();
        PeriodDateConverter.Parse("1 August 2026 18:00", PeriodEdge.End)
            .Should().Be(new DateTimeOffset(2026, 8, 1, 18, 0, 0, TimeSpan.Zero));
    }

    // ---- Absent and unreadable values ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("N/A")]
    [InlineData("none")]
    [InlineData("null")]
    public void Absent_values_mean_unbounded(string? raw)
    {
        PeriodDateConverter.Parse(raw, PeriodEdge.Start).Should().BeNull();
        PeriodDateConverter.Parse(raw, PeriodEdge.End).Should().BeNull();
    }

    [Theory]
    [InlineData("spring 2027")]
    [InlineData("next Friday")]
    [InlineData("2026-13")]
    [InlineData("2026-02-30")]
    [InlineData("0000")]
    public void Unreadable_values_drop_the_date_and_record_why(string raw)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == nameof(PeriodDateConverterTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(nameof(PeriodDateConverterTests));
        using var activity = source.StartActivity("extract");

        PeriodDateConverter.Parse(raw, PeriodEdge.Start, "valid_from").Should().BeNull();

        activity!.Events.Should().ContainSingle(e => e.Name == "memory.extract.date_dropped")
            .Which.Tags.Should().Contain(new KeyValuePair<string, object?>("memory.extract.field", "valid_from"));
    }

    // ---- Through the shared parser: one bad date no longer loses the response ----

    private const string PartialDateResponse =
        """
        {
          "entities": [
            {"name":"Alice","type":"PERSON","confidence":0.95,"aliases":[]},
            {"name":"Acme","type":"ORGANIZATION","confidence":0.9,"aliases":[]}
          ],
          "facts": [
            {"subject":"Alice","predicate":"works_at","object":"Acme","valid_from":"2026-08","confidence":0.95},
            {"subject":"Alice","predicate":"contract_ends","object":"Acme","valid_until":"2027","confidence":0.8},
            {"subject":"Alice","predicate":"started_climbing","object":"climbing","valid_from":2024,"confidence":0.8},
            {"subject":"Alice","predicate":"plans","object":"trip","valid_from":"spring 2027","confidence":0.7}
          ],
          "preferences": [
            {"category":"drink","preference":"tea","confidence":0.9}
          ],
          "relations": []
        }
        """;

    [Fact]
    public void TryParse_accepts_reduced_precision_and_unreadable_dates_without_losing_items()
    {
        LlmExtractionRunner.TryParse(PartialDateResponse, out var dto).Should().BeTrue();

        dto!.Entities.Should().HaveCount(2);
        dto.Preferences.Should().HaveCount(1);
        var facts = dto.Facts!;
        facts.Should().HaveCount(4);
        facts[0].ValidFrom.Should().Be(Aug1);
        facts[1].ValidUntil.Should().Be(new DateTimeOffset(2027, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999));
        facts[2].ValidFrom.Should().Be(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        facts[3].ValidFrom.Should().BeNull("an unreadable date drops only that date, never the fact");
    }

    [Fact]
    public void A_non_string_date_token_is_skipped_without_breaking_the_rest_of_the_document()
    {
        const string json =
            """{"facts":[{"subject":"A","predicate":"p","object":"B","valid_from":{"year":2026},"valid_until":true,"confidence":0.9}]}""";

        LlmExtractionRunner.TryParse(json, out var dto).Should().BeTrue();
        dto!.Facts.Should().ContainSingle();
        dto.Facts![0].ValidFrom.Should().BeNull();
        dto.Facts[0].ValidUntil.Should().BeNull();
        dto.Facts[0].Confidence.Should().Be(0.9);
    }

    [Fact]
    public async Task Unified_extraction_keeps_the_whole_response_in_one_call()
    {
        // Pre-fix: three calls (two misleading re-prompts), then FormatException and nothing stored.
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, PartialDateResponse))));
        var sut = new LlmUnifiedMemoryExtractor(
            client,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, MaxRetries = 2 }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        var result = await sut.ExtractAsync(
        [
            new Message
            {
                MessageId = "m-1", ConversationId = "c-1", SessionId = "s-1", Role = "user",
                Content = "Alice started at Acme in August 2026.", TimestampUtc = DateTimeOffset.UtcNow,
            },
        ]);

        await client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
        result.Entities.Should().HaveCount(2);
        result.Facts.Should().HaveCount(4);
        result.Facts[0].ValidFrom.Should().Be(Aug1);
        result.Preferences.Should().HaveCount(1);
    }
}
