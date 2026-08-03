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
            Arg.Is<ChatOptions>(options => options.ResponseFormat == ChatResponseFormat.Json),
            Arg.Any<CancellationToken>());
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
        var sut = CreateSut(client);

        var results = await sut.ExtractAsync(requests, maxSessionsPerBatch: 2, maxInputTokens: 100_000);

        results.Should().HaveCount(2);
        results[requests[0].SessionId].Facts.Should().ContainSingle();
        results[requests[1].SessionId].Facts.Should().ContainSingle();
        await client.Received(3).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
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
        bool batched = true) =>
        new(
            client,
            Options.Create(new LlmExtractionOptions
            {
                UseUnifiedExtraction = unified,
                UseMultiSessionBatchExtraction = batched,
                MaxRetries = 0,
            }),
            NullLogger<LlmMultiSessionUnifiedMemoryExtractor>.Instance);

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
        return Payload(requests.Where(request => prompt.Contains(request.SessionId, StringComparison.Ordinal)).ToArray());
    }

    private static string Payload(
        IReadOnlyList<ExtractionRequest> requests,
        IReadOnlyList<string>? acknowledged = null)
    {
        acknowledged ??= requests.Select(request => request.SessionId).ToArray();
        var acks = string.Join(',', acknowledged.Select(key => $"\"{key}\""));
        var entities = string.Join(',', requests.SelectMany(request =>
        {
            var index = request.SessionId[^2..];
            return new[]
            {
                $"{{\"source_session\":\"{request.SessionId}\",\"name\":\"Person {index}\",\"type\":\"PERSON\",\"confidence\":0.95}}",
                $"{{\"source_session\":\"{request.SessionId}\",\"name\":\"Company {index}\",\"type\":\"ORGANIZATION\",\"confidence\":0.95}}",
            };
        }));
        var facts = string.Join(',', requests.Select(request =>
        {
            var index = request.SessionId[^2..];
            return $"{{\"source_session\":\"{request.SessionId}\",\"subject\":\"Person {index}\",\"predicate\":\"works_at\",\"object\":\"Company {index}\",\"confidence\":0.9}}";
        }));
        var preferences = string.Join(',', requests.Select(request =>
            $"{{\"source_session\":\"{request.SessionId}\",\"category\":\"drink\",\"preference\":\"tea\",\"confidence\":0.9}}"));
        var relations = string.Join(',', requests.Select(request =>
        {
            var index = request.SessionId[^2..];
            return $"{{\"source_session\":\"{request.SessionId}\",\"source\":\"Person {index}\",\"target\":\"Company {index}\",\"relation_type\":\"WORKS_AT\",\"confidence\":0.9}}";
        }));
        return $"{{\"processed_source_sessions\":[{acks}],\"entities\":[{entities}],\"facts\":[{facts}],\"preferences\":[{preferences}],\"relations\":[{relations}]}}";
    }

    private static ChatResponse Response(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));
}
