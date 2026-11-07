using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Security;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

public sealed class PendingTurnVisibilityTests
{
    [Fact]
    public async Task Next_turn_sees_outstanding_write_even_when_recall_returns_no_history()
    {
        await using var queue = new BackgroundExtractionQueue(1, TimeSpan.FromSeconds(2),
            NullLogger<BackgroundExtractionQueue>.Instance);
        var memory = Substitute.For<IMemoryService>();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await finish.Task.WaitAsync(call.Arg<CancellationToken>());
                return new ExtractionResult { SourceMessageIds = ["m1"] };
            });
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult { Context = new MemoryContext { SessionId = "s1", AssembledAtUtc = DateTimeOffset.UnixEpoch } });
        var options = new AgentFrameworkOptions { RecallWaitsForPendingExtraction = TimeSpan.Zero, IncludePendingTurns = true };
        var provider = new Neo4jMemoryContextProvider(memory, Substitute.For<IEmbeddingOrchestrator>(),
            new TimeProviderClock(Substitute.For<TimeProvider>()), Substitute.For<IIdGenerator>(), Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()), Options.Create(options),
            NullLogger<Neo4jMemoryContextProvider>.Instance, backgroundExtraction: queue);
        try
        {
            await TurnExtraction.ExtractAsync(memory, new ExtractionRequest
            {
                SessionId = "s1", UserId = "alice", Messages = [new Message
                {
                    MessageId = "m1", SessionId = "s1", ConversationId = "c1", Role = "user",
                    Content = "My new address is in Porto.", TimestampUtc = DateTimeOffset.UnixEpoch,
                }],
            }, options, queue, NullLogger.Instance, CancellationToken.None);
            await TurnExtraction.WaitForPendingAsync(queue, options, "alice", "s1", CancellationToken.None);
            var context = await provider.BuildContextAsync([new ChatMessage(ChatRole.User, "Where do I live?")],
                "s1", "c1", CancellationToken.None, "alice");
            string.Join("\n", context.Messages?.Select(m => m.Text) ?? []).Should().Contain("My new address is in Porto.");
            finish.TrySetResult();
            await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var after = await provider.BuildContextAsync([new ChatMessage(ChatRole.User, "Where do I live?")],
                "s1", "c1", CancellationToken.None, "alice");
            string.Join("\n", after.Messages?.Select(m => m.Text) ?? []).Should().NotContain("My new address is in Porto.");
        }
        finally
        {
            finish.TrySetResult();
            await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static ExtractionRequest Request(string text = "I live in Porto.") => new()
    {
        SessionId = "s1", UserId = "alice", Messages = [new Message
        {
            MessageId = "m1", SessionId = "s1", ConversationId = "c1", Role = "user",
            Content = text, TimestampUtc = DateTimeOffset.UnixEpoch,
        }],
    };

    [Fact]
    public void Failing_pending_admission_preserves_the_context_already_assembled()
    {
        var policy = Substitute.For<IMemoryContextAdmissionPolicy>();
        policy.Evaluate(Arg.Any<MemoryAdmissionContext>()).Returns(_ =>
            throw new InvalidOperationException("admission unavailable"));
        IReadOnlyList<ChatMessage> existing = [new(ChatRole.User, "Existing context")];
        var result = PendingTurnContext.Append(Request().Messages, existing, [], new ContextFormatOptions(),
            policy, NullLogger.Instance, mark: true);
        result.Should().BeSameAs(existing);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("history")]
    [InlineData("facade")]
    public async Task Every_context_surface_respects_store_owner_session_and_the_off_switch(string surface)
    {
        await using var queue = new BackgroundExtractionQueue(1, TimeSpan.FromSeconds(2),
            NullLogger<BackgroundExtractionQueue>.Instance);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueueTurn("owner:alice", token => finish.Task.WaitAsync(token), Request(), "app-a").Should().BeTrue();
        var memory = Substitute.For<IMemoryService>();
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult { Context = new MemoryContext { SessionId = "s1", AssembledAtUtc = DateTimeOffset.UnixEpoch } });
        var store = Substitute.For<IMemoryStoreContext>();
        var options = new AgentFrameworkOptions { IncludePendingTurns = true };
        var clock = Substitute.For<TimeProvider>();
        var ids = Substitute.For<IIdGenerator>();
        var context = new Neo4jMemoryContextProvider(memory, Substitute.For<IEmbeddingOrchestrator>(), new TimeProviderClock(clock), ids,
            Options.Create(new MemoryOptions()), Options.Create(options.ContextFormat), Options.Create(options),
            NullLogger<Neo4jMemoryContextProvider>.Instance, storeContext: store, backgroundExtraction: queue);
        var history = new Neo4jChatHistoryProvider(memory, new TimeProviderClock(clock), ids, options,
            NullLogger<Neo4jChatHistoryProvider>.Instance, storeContext: store, backgroundExtraction: queue);
        var messageStore = new Neo4jChatMessageStore(memory, new TimeProviderClock(clock), ids, NullLogger<Neo4jChatMessageStore>.Instance);
        var facade = new Neo4jMicrosoftMemoryFacade(memory, messageStore, Options.Create(options),
            NullLogger<Neo4jMicrosoftMemoryFacade>.Instance, backgroundExtraction: queue, storeContext: store);
        async Task<string> Read(string app = "app-a", string owner = "alice", string session = "s1")
        {
            store.ApplicationId.Returns(app);
            ChatMessage[] question = [new(ChatRole.User, "Where do I live?")];
            IEnumerable<ChatMessage> messages = surface switch
            {
                "context" => (await context.BuildContextAsync(question, session, "c1", CancellationToken.None, owner)).Messages ?? [],
                "history" => await history.PerformProvideAsync(session, owner),
                _ => await facade.GetContextForRunAsync(question, session, "c1", owner),
            };
            return string.Join("\n", messages.Select(m => m.Text));
        }
        try
        {
            (await Read()).Should().Contain("I live in Porto.");
            (await Read(app: "app-b")).Should().NotContain("I live in Porto.");
            (await Read(owner: "bob")).Should().NotContain("I live in Porto.");
            (await Read(session: "s2")).Should().NotContain("I live in Porto.");
            options.IncludePendingTurns = false;
            (await Read()).Should().NotContain("I live in Porto.");
        }
        finally
        {
            finish.TrySetResult();
            await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Pending_context_deduplicates_live_and_recalled_turns_and_applies_admission_before_budget()
    {
        var oldest = Request().Messages[0];
        var pending = new[]
        {
            oldest with { MessageId = "4", Content = "excluded" },
            oldest with { MessageId = "3", Content = "already live" },
            oldest with { MessageId = "2", Content = "already recalled" },
            oldest,
        };
        var policy = Substitute.For<IMemoryContextAdmissionPolicy>();
        policy.Evaluate(Arg.Any<MemoryAdmissionContext>()).Returns(call =>
            new MemoryAdmissionDecision { Include = call.Arg<MemoryAdmissionContext>().Content != "excluded" });
        var result = PendingTurnContext.Append(pending, [new ChatMessage(ChatRole.User, "already recalled")],
            [new ChatMessage(ChatRole.User, "already live")], new ContextFormatOptions { MaxChatHistoryMessages = 1 },
            policy, NullLogger.Instance, mark: true);
        result.Count(m => m.Text == "already recalled").Should().Be(1);
        result.Should().Contain(m => m.Text == "I live in Porto." && m.Role == ChatRole.User);
        result.Should().NotContain(m => m.Text == "excluded" || m.Text == "already live");
    }

    [Fact]
    public async Task Failed_and_cancelled_attempts_do_not_leave_stale_pending_entries()
    {
        await using var queue = new BackgroundExtractionQueue(1, TimeSpan.Zero,
            NullLogger<BackgroundExtractionQueue>.Instance);
        queue.TryEnqueueTurn("owner:alice", _ => Task.FromException(new InvalidOperationException("writer failed")),
            Request(), null);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        queue.GetPendingTurns(null, "alice", "s1").Should().BeEmpty();

        queue.TryEnqueueTurn("owner:alice", token => Task.Delay(Timeout.InfiniteTimeSpan, token), Request(), null);
        queue.TryEnqueueTurn("owner:alice", token => Task.Delay(Timeout.InfiniteTimeSpan, token), Request(), null);
        await queue.DisposeAsync();
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        queue.GetPendingTurns(null, "alice", "s1").Should().BeEmpty();
        queue.TryEnqueueTurn("owner:alice", _ => Task.CompletedTask, Request(), null).Should().BeFalse();
        queue.GetPendingTurns(null, "alice", "s1").Should().BeEmpty();
    }
}
