using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class LlmMultiSessionUnifiedMemoryExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ConcurrentBatchesOverlapAndRestorePlanOrder()
    {
        const int expectedConcurrency = 4;
        var requests = Requests(8);
        var client = Substitute.For<IChatClient>();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var active = 0;
        var maximumActive = 0;
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var nowActive = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, nowActive);
                if (Interlocked.Increment(ref entered) == expectedConcurrency)
                    release.TrySetResult();
                await release.Task;
                Interlocked.Decrement(ref active);
                return Response(PayloadForPrompt(
                    call.Arg<IEnumerable<ChatMessage>>(), requests));
            });
        var sut = CreateSut(client, maxConcurrentBatches: expectedConcurrency);

        var results = await sut.ExtractAsync(
            requests, maxSessionsPerBatch: 2, maxInputTokens: 100_000);

        maximumActive.Should().Be(expectedConcurrency);
        results.Keys.Should().Equal(requests.Select(request => request.SessionId));
        await client.Received(expectedConcurrency).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_GlobalLimiterCapsConcurrentBatches()
    {
        const int expectedGlobalConcurrency = 2;
        var requests = Requests(8);
        var client = Substitute.For<IChatClient>();
        var active = 0;
        var maximumActive = 0;
        var sync = new object();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var nowActive = Interlocked.Increment(ref active);
                lock (sync)
                {
                    maximumActive = Math.Max(maximumActive, nowActive);
                }
                await Task.Delay(25);
                Interlocked.Decrement(ref active);
                return Response(PayloadForPrompt(
                    call.Arg<IEnumerable<ChatMessage>>(), requests));
            });
        var sut = CreateSut(
            client,
            maxConcurrentBatches: 4,
            maxConcurrentExtractionBatches: expectedGlobalConcurrency);

        var results = await sut.ExtractAsync(
            requests, maxSessionsPerBatch: 2, maxInputTokens: 100_000);

        maximumActive.Should().Be(expectedGlobalConcurrency);
        results.Keys.Should().Equal(requests.Select(request => request.SessionId));
        await client.Received(4).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_EightSessionsAtBatchFour_UsesTwoCallsAndKeepsKeysExact()
    {
        var requests = Requests(8);
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Response(PayloadForPrompt(
                call.Arg<IEnumerable<ChatMessage>>(), requests))));
        var sut = CreateSut(client);

        var results = await sut.ExtractAsync(requests, maxSessionsPerBatch: 4, maxInputTokens: 100_000);

        results.Keys.Should().BeEquivalentTo(requests.Select(request => request.SessionId));
        results.Values.Should().AllSatisfy(result =>
        {
            result.Entities.Should().HaveCount(2);
            result.Facts.Should().ContainSingle();
            result.Preferences.Should().ContainSingle();
            result.Relationships.Should().ContainSingle();
        });
        await client.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions>(options => options.ResponseFormat != null &&
                options.ResponseFormat.GetType() == typeof(ChatResponseFormatJson) &&
                ((ChatResponseFormatJson)options.ResponseFormat).Schema.HasValue),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_BatchRequestUsesShortAliasesAndConstrainedSchema()
    {
        var requests = Requests(2);
        var client = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        string? capturedPrompt = null;
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedOptions = call.Arg<ChatOptions>();
                capturedPrompt = string.Join('\n', call.Arg<IEnumerable<ChatMessage>>().Select(message => message.Text));
                return Task.FromResult(Response(
                    "{\"processed_source_sessions\":[\"s1\",\"s2\"],\"entities\":[],\"facts\":[],\"preferences\":[],\"relations\":[]}"));
            });
        var sut = CreateSut(client);

        var results = await sut.ExtractAsync(
            requests, maxSessionsPerBatch: 2, maxInputTokens: 100_000);

        results.Keys.Should().Equal(requests.Select(request => request.SessionId));
        capturedPrompt.Should().Contain("<source_session key=\"s1\">")
            .And.Contain("<source_session key=\"s2\">");
        capturedPrompt.Should().NotContain(requests[0].SessionId).And.NotContain(requests[1].SessionId);
        var format = capturedOptions!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>().Which;
        format.Schema.Should().NotBeNull();
        var schema = format.Schema!.Value;
        var allowed = schema.GetProperty("properties")
            .GetProperty("entities")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("source_session")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString());
        allowed.Should().Equal("s1", "s2");
        schema.GetRawText().Should().NotContain(requests[0].SessionId).And.NotContain(requests[1].SessionId);
    }
    [Fact]
    public async Task ExtractAsync_JsonResponseFormatDisabledLeavesRequestUnspecified()
    {
        var requests = Requests(2);
        var client = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedOptions = call.Arg<ChatOptions>();
                return Task.FromResult(Response(
                    "{\"processed_source_sessions\":[\"s1\",\"s2\"],\"entities\":[],\"facts\":[],\"preferences\":[],\"relations\":[]}"));
            });
        var sut = CreateSut(client, useJsonResponseFormat: false);

        var results = await sut.ExtractAsync(
            requests, maxSessionsPerBatch: 2, maxInputTokens: 100_000);

        results.Keys.Should().Equal(requests.Select(request => request.SessionId));
        capturedOptions!.ResponseFormat.Should().BeNull();
    }


    [Fact]
    public async Task ExtractAsync_MissingAcknowledgement_RecursivelySplitsAndLosesNothing()
    {
        var requests = Requests(2);
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(Response(Payload([requests[0]], acknowledged: []))),
                Task.FromResult(Response(Payload([requests[0]]))),
                Task.FromResult(Response(Payload([requests[1]]))));
        var diagnostics = new LlmExtractionBatchDiagnostics();
        var sut = CreateSut(client, diagnostics: diagnostics);

        var results = await sut.ExtractAsync(requests, maxSessionsPerBatch: 2, maxInputTokens: 100_000);

        results.Should().HaveCount(2);
        results[requests[0].SessionId].Facts.Should().ContainSingle();
        results[requests[1].SessionId].Facts.Should().ContainSingle();
        await client.Received(3).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
        var diagnostic = diagnostics.Snapshot();
        diagnostic.Splits.Should().Be(1);
        diagnostic.DroppedDetails.Should().Be(0);
        var detail = diagnostic.Details.Should().ContainSingle().Which;
        detail.Reason.Should().Be("acknowledgement");
        detail.SourceSessions.Should().Be(2);
        detail.ExceptionType.Should().EndWith("+BatchValidationException");
    }

    [Fact]
    public async Task ExtractAsync_SingleSessionOverTokenBudget_FailsBeforeProviderCall()
    {
        var client = Substitute.For<IChatClient>();
        var sut = CreateSut(client);

        var act = () => sut.ExtractAsync(Requests(1), maxSessionsPerBatch: 1, maxInputTokens: 1);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds*token budget*");
        await client.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsEnabled_RequiresUnifiedAndMultiSessionSwitches()
    {
        var client = Substitute.For<IChatClient>();

        CreateSut(client, unified: false, batched: true).IsEnabled.Should().BeFalse();
        CreateSut(client, unified: true, batched: false).IsEnabled.Should().BeFalse();
        CreateSut(client, unified: true, batched: true).IsEnabled.Should().BeTrue();
    }

    private static LlmMultiSessionUnifiedMemoryExtractor CreateSut(
        IChatClient client,
        bool unified = true,
        bool batched = true,
        int maxConcurrentBatches = 1,
        int maxConcurrentExtractionBatches = 0,
        LlmExtractionBatchDiagnostics? diagnostics = null,
        bool useJsonResponseFormat = true)
    {
        var options = Options.Create(new LlmExtractionOptions
        {
            UseJsonResponseFormat = useJsonResponseFormat,
            UseUnifiedExtraction = unified,
            UseMultiSessionBatchExtraction = batched,
            MaxConcurrentBatchesPerExtraction = maxConcurrentBatches,
            MaxConcurrentExtractionBatches = maxConcurrentExtractionBatches,
            MaxRetries = 0,
        });
        var limiter = new LlmExtractionBatchConcurrencyLimiter(options);
        return new LlmMultiSessionUnifiedMemoryExtractor(
            client,
            options,
            NullLogger<LlmMultiSessionUnifiedMemoryExtractor>.Instance,
            limiter,
            diagnostics);
    }

    private static IReadOnlyList<ExtractionRequest> Requests(int count) =>
        Enumerable.Range(0, count).Select(index =>
        {
            var session = $"session-{index:D2}";
            return new ExtractionRequest
            {
                SessionId = session,
                UserId = $"owner-{index:D2}",
                Messages =
                [
                    new Message
                    {
                        MessageId = $"{session}-message",
                        ConversationId = $"{session}-conversation",
                        SessionId = session,
                        Role = "user",
                        Content = $"Person {index:D2} works at Company {index:D2} and prefers tea.",
                        TimestampUtc = new DateTimeOffset(2026, 1, 1, 0, index, 0, TimeSpan.Zero),
                    },
                ],
            };
        }).ToArray();

    private static string PayloadForPrompt(
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<ExtractionRequest> requests)
    {
        var prompt = string.Join('\n', messages.Select(message => message.Text));
        var selected = requests.Where(request =>
            prompt.Contains(request.Messages[0].Content, StringComparison.Ordinal)).ToArray();
        return Payload(selected);
    }

    private static string Payload(
        IReadOnlyList<ExtractionRequest> requests,
        IReadOnlyList<string>? acknowledged = null)
    {
        var keyed = requests.Select((request, index) => new
        {
            Request = request,
            SourceKey = LlmMultiSessionExtractionResponseContract.Alias(index)
        }).ToArray();
        acknowledged ??= keyed.Select(item => item.SourceKey).ToArray();
        var acks = string.Join(',', acknowledged.Select(key => $"\"{key}\""));
        var entities = string.Join(',', keyed.SelectMany(item =>
        {
            var index = item.Request.SessionId[^2..];
            return new[]
            {
                $"{{\"source_session\":\"{item.SourceKey}\",\"name\":\"Person {index}\",\"type\":\"PERSON\",\"confidence\":0.95}}",
                $"{{\"source_session\":\"{item.SourceKey}\",\"name\":\"Company {index}\",\"type\":\"ORGANIZATION\",\"confidence\":0.95}}",
            };
        }));
        var facts = string.Join(',', keyed.Select(item =>
        {
            var index = item.Request.SessionId[^2..];
            return $"{{\"source_session\":\"{item.SourceKey}\",\"subject\":\"Person {index}\",\"predicate\":\"works_at\",\"object\":\"Company {index}\",\"confidence\":0.9}}";
        }));
        var preferences = string.Join(',', keyed.Select(item =>
            $"{{\"source_session\":\"{item.SourceKey}\",\"category\":\"drink\",\"preference\":\"tea\",\"confidence\":0.9}}"));
        var relations = string.Join(',', keyed.Select(item =>
        {
            var index = item.Request.SessionId[^2..];
            return $"{{\"source_session\":\"{item.SourceKey}\",\"source\":\"Person {index}\",\"target\":\"Company {index}\",\"relation_type\":\"WORKS_AT\",\"confidence\":0.9}}";
        }));
        return $"{{\"processed_source_sessions\":[{acks}],\"entities\":[{entities}],\"facts\":[{facts}],\"preferences\":[{preferences}],\"relations\":[{relations}]}}";
    }

    private static ChatResponse Response(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));
}
