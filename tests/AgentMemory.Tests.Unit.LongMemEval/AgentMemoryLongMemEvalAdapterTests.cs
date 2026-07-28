using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class AgentMemoryLongMemEvalAdapterTests
{
    [Fact]
    public async Task InvokeAsync_PersistsInjectedHistoryAndAnswersOnlyFromRecalledMemory()
    {
        var memory = Substitute.For<IMemoryService>();
        IReadOnlyList<Message>? stored = null;
        RecallRequest? recallRequest = null;
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                stored = call.Arg<IEnumerable<Message>>().ToArray();
                return stored;
            });
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recallRequest = call.Arg<RecallRequest>();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = recallRequest.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items =
                            [
                                Message(
                                    recallRequest.SessionId,
                                    "assistant",
                                    "Alice moved to Zurich in March.")
                            ]
                        }
                    },
                    TotalItemsRetrieved = 1
                };
            });

        var chat = Substitute.For<IChatClient>();
        IReadOnlyList<ChatMessage>? answerPrompt = null;
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                answerPrompt = call.Arg<IEnumerable<ChatMessage>>().ToArray();
                return new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, "Alice lives in Zurich."));
            });

        var adapter = new AgentMemoryLongMemEvalAdapter(memory, chat, "test-run");
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(
        [
            ("Alice moved to Zurich in March.", "Thanks, I will remember that."),
            ("Her favorite color is blue.", "Understood.")
        ]);

        var response = await adapter.InvokeAsync("Where does Alice live?");

        response.Text.Should().Be("Alice lives in Zurich.");
        stored.Should().HaveCount(4);
        stored!.Select(message => message.SessionId).Distinct().Should().ContainSingle();
        recallRequest.Should().NotBeNull();
        recallRequest!.Options.BlendMode.Should().Be(RetrievalBlendMode.MemoryOnly);
        recallRequest.Options.MaxRecentMessages.Should().Be(0);
        recallRequest.Options.MaxEntities.Should().Be(0);
        answerPrompt.Should().NotBeNull();
        answerPrompt!.Select(message => message.Text).Should()
            .Contain(text => text!.Contains("Alice moved to Zurich", StringComparison.Ordinal));
        adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new LongMemEvalQuestionTelemetry(1, 4, 1, false));
    }

    [Fact]
    public async Task InvokeAsync_RejectsAQuestionWithoutInjectedHistory()
    {
        var adapter = new AgentMemoryLongMemEvalAdapter(
            Substitute.For<IMemoryService>(),
            Substitute.For<IChatClient>(),
            "test-run");
        await adapter.ResetSessionAsync();

        var act = () => adapter.InvokeAsync("What should I remember?");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*history*");
    }

    [Fact]
    public async Task ResetSessionAsync_IsolatesQuestionsWithDistinctSessionAndOwnerScopes()
    {
        var memory = Substitute.For<IMemoryService>();
        var requests = new List<RecallRequest>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RecallRequest>();
                requests.Add(request);
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = request.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = [Message(request.SessionId, "user", request.Query)]
                        }
                    },
                    TotalItemsRetrieved = 1
                };
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        var adapter = new AgentMemoryLongMemEvalAdapter(memory, chat, "test-run");

        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("one", "first")]);
        await adapter.InvokeAsync("question one");
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("two", "second")]);
        await adapter.InvokeAsync("question two");

        requests.Should().HaveCount(2);
        requests.Select(request => request.SessionId).Distinct().Should().HaveCount(2);
        requests.Select(request => request.UserId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task InvokeAsync_RecordsEmptyRetrievalInTelemetry()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RecallRequest>();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = request.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = []
                        }
                    },
                    TotalItemsRetrieved = 0
                };
            });
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            Substitute.For<IChatClient>(),
            "test-run");
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("one", "first")]);

        var act = () => adapter.InvokeAsync("question one");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*retrieved no history*");
        adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                QuestionNumber = 1,
                MessagesStored = 2,
                ItemsRetrieved = 0,
                RecallTruncated = false,
                Status = "retrieval-empty"
            });
    }

    [Fact]
    public async Task InvokeAsync_RecordsSanitizedAnswerFailureInTelemetry()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RecallRequest>();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = request.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = [Message(request.SessionId, "user", "remembered detail")]
                        }
                    },
                    TotalItemsRetrieved = 1
                };
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(
                new InvalidOperationException("provider-secret-detail")));
        var adapter = new AgentMemoryLongMemEvalAdapter(memory, chat, "test-run");
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("one", "first")]);

        var act = () => adapter.InvokeAsync("question one");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LongMemEval answer stage failed.");
        adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                QuestionNumber = 1,
                MessagesStored = 2,
                ItemsRetrieved = 1,
                RecallTruncated = false,
                Status = "answer-error"
            });
    }

    private static Message Message(string sessionId, string role, string content) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        SessionId = sessionId,
        ConversationId = sessionId,
        Role = role,
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch
    };
}
