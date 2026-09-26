using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using AgentMemory.Extraction.Llm.Internal;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// What the extraction runner does with a reply it cannot fully read.
/// </summary>
/// <remarks>
/// Captured live before these changes: one unreadable field failed the whole typed parse, the runner
/// said "not valid JSON" (it was valid), echoed the whole reply back (the prompt grew every retry),
/// and after three attempts the extractor discarded everything. The fixtures below reproduce those
/// reply shapes with synthetic content; the chat client is a scripted fake, so nothing here calls a model.
/// </remarks>
public sealed class ExtractionRepairTests
{
    private static readonly Message Turn = new()
    {
        MessageId = "m-1", ConversationId = "c-1", SessionId = "s-1", Role = "user",
        Content = "Alice started at Acme in August.", TimestampUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>A scripted chat client that records every request.</summary>
    private sealed class ScriptedChat(params ChatResponse[] replies) : IChatClient
    {
        private int _next;
        public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((messages.ToList(), options is null ? null : options.Clone()));
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatResponse Reply(string text, ChatFinishReason? finish = null, long? outputTokens = null) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = finish ?? ChatFinishReason.Stop,
            Usage = outputTokens is null ? null : new UsageDetails { OutputTokenCount = outputTokens },
        };

    private static LlmUnifiedMemoryExtractor Extractor(IChatClient chat, int maxRetries = 2) =>
        new(chat, Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, MaxRetries = maxRetries }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

    // ---- Salvage: a schema failure keeps what it can ----

    private const string OneBadFact =
        """
        {"entities":[{"name":"Alice","type":"PERSON","confidence":0.9},{"name":"Acme","type":"ORGANIZATION","confidence":"high"}],
         "facts":[{"subject":"Alice","predicate":"works_at","object":"Acme","confidence":0.9},
                  {"subject":"Alice","predicate":"likes","object":"tea","confidence":"very"}],
         "preferences":[{"category":"drink","preference":"tea","confidence":0.8}],
         "relations":[]}
        """;

    [Fact]
    public void A_schema_failure_is_salvaged_item_by_item_and_names_what_it_dropped()
    {
        var parsed = LlmExtractionRunner.Parse(OneBadFact);

        parsed.Status.Should().Be(LlmExtractionRunner.ParseStatus.Salvaged);
        parsed.IsUsable.Should().BeTrue();
        parsed.Response!.Entities.Should().ContainSingle(e => e.Name == "Alice");
        parsed.Response.Facts.Should().ContainSingle(f => f.Predicate == "works_at");
        parsed.Response.Preferences.Should().ContainSingle();
        parsed.Dropped.Should().HaveCount(2);
        parsed.Dropped.Should().Contain(d => d.StartsWith("entities[1].confidence", StringComparison.Ordinal));
        parsed.Dropped.Should().Contain(d => d.StartsWith("facts[1].confidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_salvaged_reply_is_used_without_any_retry()
    {
        var chat = new ScriptedChat(Reply(OneBadFact));

        var result = await Extractor(chat).ExtractAsync([Turn]);

        chat.Requests.Should().ContainSingle("salvage made a second call unnecessary");
        result.Entities.Should().ContainSingle();
        result.Facts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Sorry, I cannot help with that.", "no JSON object")]
    [InlineData("{ \"entities\": [ {\"name\": \"A\" ", "no JSON object")]      // truncated mid-document
    [InlineData("{invalid}", "not valid JSON")]
    [InlineData("[\"just\", \"strings\"]", "JSON array")]
    public void Unreadable_replies_say_what_is_wrong(string raw, string expected)
    {
        var parsed = LlmExtractionRunner.Parse(raw);

        parsed.Status.Should().Be(LlmExtractionRunner.ParseStatus.Unusable);
        parsed.Error.Should().Contain(expected);
    }

    [Fact]
    public void A_clean_empty_reply_is_a_valid_answer_not_a_failure()
    {
        var parsed = LlmExtractionRunner.Parse("""{"entities":[],"facts":[],"preferences":[],"relations":[]}""");

        parsed.IsUsable.Should().BeTrue();
        parsed.KeptItems.Should().Be(0);
    }

    [Fact]
    public void A_salvage_that_dropped_everything_is_not_usable()
    {
        var parsed = LlmExtractionRunner.Parse("""{"facts":[{"subject":"A","predicate":"p","object":"B","confidence":"x"}]}""");

        parsed.Status.Should().Be(LlmExtractionRunner.ParseStatus.Salvaged);
        parsed.IsUsable.Should().BeFalse();
    }

    // ---- Repair: targeted, not echoed, not accumulated ----

    [Fact]
    public async Task The_repair_message_names_the_problem_and_never_echoes_the_bad_reply()
    {
        const string bad = """{"facts":[{"subject":"A","predicate":"p","object":"B","confidence":"x"}]}""";
        var chat = new ScriptedChat(Reply(bad), Reply(bad), Reply("""{"facts":[]}"""));

        await Extractor(chat).ExtractAsync([Turn]);

        chat.Requests.Should().HaveCount(3);
        chat.Requests[0].Messages.Should().HaveCount(2);
        foreach (var (messages, _) in chat.Requests.Skip(1))
        {
            messages.Should().HaveCount(3, "one repair message, replaced each attempt, never accumulated");
            messages.Should().NotContain(m => m.Role == ChatRole.Assistant, "the bad reply is not echoed back");
            var repair = messages[^1];
            LlmExtractionRunner.IsRepairInstruction(repair).Should().BeTrue();
            repair.Text.Should().Contain("facts[0].confidence");
            repair.Text.Should().NotContain("not valid JSON", "it was valid JSON; the old message misled the model");
        }
    }

    // ---- Finish reasons ----

    [Fact]
    public async Task A_truncated_reply_is_retried_with_twice_the_output_room()
    {
        var chat = new ScriptedChat(
            Reply("""{"entities":[{"name":"Alice","ty""", ChatFinishReason.Length, outputTokens: 1500),
            Reply("""{"entities":[{"name":"Alice","type":"PERSON","confidence":0.9}]}"""));

        var result = await Extractor(chat).ExtractAsync([Turn]);

        result.Entities.Should().ContainSingle();
        chat.Requests[0].Options!.MaxOutputTokens.Should().BeNull("the default request is unchanged");
        chat.Requests[1].Options!.MaxOutputTokens.Should().Be(3000);
        chat.Requests[1].Messages[^1].Text.Should().Contain("cut off");
    }

    [Fact]
    public async Task Truncation_retries_never_exceed_the_ceiling()
    {
        var cut = Reply("""{"entities":[""", ChatFinishReason.Length, outputTokens: 30_000);
        var chat = new ScriptedChat(cut, cut, cut);

        var act = () => Extractor(chat).ExtractAsync([Turn]);

        await act.Should().ThrowAsync<FormatException>().WithMessage("*cut off*");
        chat.Requests[2].Options!.MaxOutputTokens.Should().Be(LlmExtractionRunner.MaxOutputTokenCeiling);
    }

    [Fact]
    public async Task A_content_filter_stop_is_not_retried()
    {
        var chat = new ScriptedChat(Reply("", ChatFinishReason.ContentFilter));

        var act = () => Extractor(chat).ExtractAsync([Turn]);

        await act.Should().ThrowAsync<FormatException>().WithMessage("*content filter*");
        chat.Requests.Should().ContainSingle();
    }

    // ---- Telemetry: every attempt is a span with its outcome ----

    [Fact]
    public async Task Every_attempt_is_a_span_with_outcome_finish_reason_and_error()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is AgentMemoryDiagnostics.SourceName or nameof(ExtractionRepairTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        using var root = new ActivitySource(nameof(ExtractionRepairTests)).StartActivity("test-root");
        var chat = new ScriptedChat(Reply("no json here"), Reply(OneBadFact));

        await Extractor(chat).ExtractAsync([Turn]);

        List<Activity> attempts;
        lock (spans)
            attempts = spans.Where(s => s.OperationName == "memory.extract.attempt" && s.TraceId == root!.TraceId).ToList();
        attempts.Should().HaveCount(2);
        attempts[0].GetTagItem("memory.extract.outcome").Should().Be("syntax_error");
        attempts[0].GetTagItem("memory.extract.error").Should().Be("the reply contained no JSON object");
        attempts[0].Status.Should().Be(ActivityStatusCode.Error);
        attempts[1].GetTagItem("memory.extract.outcome").Should().Be("salvaged");
        attempts[1].GetTagItem("memory.extract.items.dropped").Should().Be(2);
        attempts[1].GetTagItem("memory.extract.finish_reason").Should().Be("stop");
    }
}
