using AgentMemory.Tests.Unit.TestSupport;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// T5.1: extraction after the turn has returned. Inline, every answer waited for memorising (seconds:
/// a model call, resolution, writes) although the reply was already complete.
/// </summary>
public sealed class BackgroundExtractionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static BackgroundExtractionQueue Queue(int concurrency = 4, TimeSpan? drain = null) =>
        new(concurrency, drain ?? TimeSpan.FromSeconds(10), NullLogger<BackgroundExtractionQueue>.Instance);

    // ---- the queue ----

    [Fact]
    public async Task One_sessions_turns_run_in_order_one_at_a_time()
    {
        await using var queue = Queue();
        var first = new TaskCompletionSource();
        var order = new List<string>();
        queue.TryEnqueue("s1", async _ => { await first.Task; lock (order) order.Add("turn 1"); });
        queue.TryEnqueue("s1", _ => { lock (order) order.Add("turn 2"); return Task.CompletedTask; });

        await Task.Delay(100);
        order.Should().BeEmpty("turn 2 waits for turn 1");
        first.SetResult();
        await queue.WhenIdleAsync().WaitAsync(Wait);

        order.Should().Equal("turn 1", "turn 2");
    }

    [Fact]
    public async Task Other_sessions_do_not_wait()
    {
        await using var queue = Queue();
        var blocked = new TaskCompletionSource();
        var other = new TaskCompletionSource();
        queue.TryEnqueue("s1", _ => blocked.Task);
        queue.TryEnqueue("s2", _ => { other.SetResult(); return Task.CompletedTask; });

        // Session 2 ran while session 1 is still blocked. (Not asserted through Pending: the finished job
        // decrements it in its finally, after signalling, so reading it here raced on a slow CI runner.)
        await other.Task.WaitAsync(Wait);
        blocked.Task.IsCompleted.Should().BeFalse();
        blocked.SetResult();
        await queue.WhenIdleAsync().WaitAsync(Wait);
        queue.Pending.Should().Be(0);
    }

    [Fact]
    public async Task Concurrency_is_capped_across_sessions()
    {
        // Which session starts first is not specified; that no two run at once is.
        await using var queue = Queue(concurrency: 1);
        var running = 0;
        var most = 0;
        async Task Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
        }
        queue.TryEnqueue("s1", _ => Work());
        queue.TryEnqueue("s2", _ => Work());
        queue.TryEnqueue("s3", _ => Work());

        await queue.WhenIdleAsync().WaitAsync(Wait);
        most.Should().Be(1);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    [Fact]
    public async Task A_failure_does_not_stop_the_next_turn()
    {
        await using var queue = Queue();
        var ran = false;
        queue.TryEnqueue("s1", _ => throw new InvalidOperationException("model down"));
        queue.TryEnqueue("s1", _ => { ran = true; return Task.CompletedTask; });

        await queue.WhenIdleAsync().WaitAsync(Wait);

        ran.Should().BeTrue();
        queue.Pending.Should().Be(0);
    }

    [Fact]
    public async Task Shutdown_drains_what_is_queued_then_refuses_new_work()
    {
        var queue = Queue();
        var gate = new TaskCompletionSource();
        var done = false;
        queue.TryEnqueue("s1", async _ => { await gate.Task; done = true; });

        var dispose = queue.DisposeAsync().AsTask();
        gate.SetResult();
        await dispose.WaitAsync(Wait);

        done.Should().BeTrue();
        queue.TryEnqueue("s1", _ => Task.CompletedTask).Should().BeFalse();
    }

    [Fact]
    public async Task Work_still_running_after_the_drain_timeout_is_cancelled()
    {
        var queue = Queue(drain: TimeSpan.FromMilliseconds(50));
        var cancelled = new TaskCompletionSource();
        queue.TryEnqueue("s1", async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
        });

        await queue.DisposeAsync().AsTask().WaitAsync(Wait);

        cancelled.Task.IsCompleted.Should().BeTrue();
    }

    // ---- the next-turn guard ----

    [Fact]
    public async Task Recall_waits_for_that_owners_pending_extraction_and_no_one_elses()
    {
        await using var queue = Queue();
        var alice = new TaskCompletionSource();
        var bob = new TaskCompletionSource();
        queue.TryEnqueue(TurnExtraction.OrderingKey("alice", "s1"), _ => alice.Task);
        queue.TryEnqueue(TurnExtraction.OrderingKey("bob", "s2"), _ => bob.Task);
        var options = new AgentFrameworkOptions { RecallWaitsForPendingExtraction = TimeSpan.FromSeconds(10) };

        var wait = TurnExtraction.WaitForPendingAsync(queue, options, "alice", "s9", CancellationToken.None);
        await Task.Delay(50);
        wait.IsCompleted.Should().BeFalse("alice's last turn is still being learned (whatever the session)");
        alice.SetResult();
        (await wait.WaitAsync(Wait)).Should().BePositive();
        bob.SetResult();
    }

    [Fact]
    public async Task Recall_waits_no_longer_than_its_budget()
    {
        await using var queue = Queue();
        var never = new TaskCompletionSource();
        queue.TryEnqueue(TurnExtraction.OrderingKey("alice", "s1"), _ => never.Task);
        var options = new AgentFrameworkOptions { RecallWaitsForPendingExtraction = TimeSpan.FromMilliseconds(100) };

        var waited = await TurnExtraction.WaitForPendingAsync(queue, options, "alice", "s1", CancellationToken.None).WaitAsync(Wait);

        waited.Should().BeLessThan(TimeSpan.FromSeconds(2));
        never.SetResult();
    }

    // ---- the provider ----

    private sealed class TestAgentSession : AgentSession;

    private sealed class StubAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session,
            AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(System.Text.Json.JsonElement serializedState,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class OwnerContext : IWritableMemoryOwnerContext
    {
        private readonly AsyncLocal<string?> _userId = new();
        public string? UserId { get => _userId.Value; set => _userId.Value = value; }
    }

    private sealed record Turn(Task Returned, TaskCompletionSource Extraction, TaskCompletionSource<string?> OwnerSeen);

    private static Turn Run(bool background, IBackgroundExtraction? queue, CancellationToken token = default)
    {
        var memory = Substitute.For<IMemoryService>().RouteIdKeyedAdds();
        var extraction = new TaskCompletionSource();
        var ownerSeen = new TaskCompletionSource<string?>();
        var owner = new OwnerContext();
        memory.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                ownerSeen.TrySetResult(owner.UserId);
                await extraction.Task;
                return new ExtractionResult { SourceMessageIds = [] };
            });
        memory.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new Message
            {
                MessageId = Guid.NewGuid().ToString("N"), SessionId = call.ArgAt<string>(0), ConversationId = call.ArgAt<string>(1),
                Role = call.ArgAt<string>(2), Content = call.ArgAt<string>(3), TimestampUtc = DateTimeOffset.UnixEpoch,
            }));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var provider = new Neo4jMemoryContextProvider(
            memory, Substitute.For<IEmbeddingOrchestrator>(), Substitute.For<IClock>(), ids,
            Options.Create(new MemoryOptions()), Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions { ExtractInBackground = background }),
            NullLogger<Neo4jMemoryContextProvider>.Instance,
            ownerContext: owner, backgroundExtraction: queue);
        var session = new TestAgentSession();
        session.WithMemoryIdentity(userId: "alice", sessionId: "s1", conversationId: "c1");

#pragma warning disable MAAI001
        var returned = provider.InvokedAsync(new AIContextProvider.InvokedContext(new StubAgent(), session,
            [new ChatMessage(ChatRole.User, "I just moved to Porto.")],
            [new ChatMessage(ChatRole.Assistant, "Porto, lovely!")]), token).AsTask();
#pragma warning restore MAAI001
        return new Turn(returned, extraction, ownerSeen);
    }

    [Fact]
    public async Task On_the_turn_returns_before_extraction_finishes_and_extraction_keeps_the_turns_owner()
    {
        await using var queue = Queue();
        var turn = Run(background: true, queue);

        await turn.Returned.WaitAsync(Wait);
        queue.Pending.Should().Be(1, "extraction is still running");
        (await turn.OwnerSeen.Task.WaitAsync(Wait)).Should().Be("alice",
            "the owner scope the turn opened was closed when it returned; the work carries its own copy");

        turn.Extraction.SetResult();
        await queue.WhenIdleAsync().WaitAsync(Wait);
    }

    /// <summary>A scheduler of the host's own that runs work on a clean context (another process would).</summary>
    private sealed class ContextFreeScheduler : IBackgroundExtraction
    {
        private readonly List<Task> _running = [];
        public int Pending => _running.Count(t => !t.IsCompleted);
        public bool TryEnqueue(string orderingKey, Func<CancellationToken, Task> work)
        {
            using (ExecutionContext.SuppressFlow())
                _running.Add(Task.Run(() => work(CancellationToken.None)));
            return true;
        }
        public Task WhenIdleAsync(CancellationToken cancellationToken = default) => Task.WhenAll(_running).WaitAsync(cancellationToken);
    }

    [Fact]
    public async Task A_scheduler_that_does_not_flow_context_still_extracts_as_the_turns_owner()
    {
        var scheduler = new ContextFreeScheduler();
        var turn = Run(background: true, scheduler);
        await turn.Returned.WaitAsync(Wait);

        (await turn.OwnerSeen.Task.WaitAsync(Wait)).Should().Be("alice");
        turn.Extraction.SetResult();
        await scheduler.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public void A_host_that_disposes_its_container_synchronously_can()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAgentMemoryFramework();
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBackgroundExtraction>();

        provider.Invoking(p => p.Dispose()).Should().NotThrow();
    }

    [Fact]
    public async Task On_cancelling_the_finished_turn_does_not_cancel_its_extraction()
    {
        await using var queue = Queue();
        using var cts = new CancellationTokenSource();
        var turn = Run(background: true, queue, cts.Token);
        await turn.Returned.WaitAsync(Wait);

        await cts.CancelAsync();
        turn.Extraction.SetResult();

        await queue.WhenIdleAsync().WaitAsync(Wait);
        turn.Returned.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Off_the_turn_waits_for_extraction_as_before()
    {
        await using var queue = Queue();
        var turn = Run(background: false, queue);

        await turn.OwnerSeen.Task.WaitAsync(Wait);
        await Task.Delay(100);
        turn.Returned.IsCompleted.Should().BeFalse();

        turn.Extraction.SetResult();
        await turn.Returned.WaitAsync(Wait);
        queue.Pending.Should().Be(0);
    }

    [Fact]
    public async Task A_queue_that_is_shutting_down_leaves_the_turn_to_extract_inline()
    {
        var queue = Queue();
        await queue.DisposeAsync();
        var turn = Run(background: true, queue);

        await turn.OwnerSeen.Task.WaitAsync(Wait);
        turn.Returned.IsCompleted.Should().BeFalse("nothing would run it otherwise");
        turn.Extraction.SetResult();
        await turn.Returned.WaitAsync(Wait);
    }

    [Fact]
    public void The_framework_registration_provides_the_queue_and_validates_its_options()
    {
        static ServiceProvider Build(Action<AgentFrameworkOptions> configure)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddAgentMemoryFramework(configure);
            return services.BuildServiceProvider();
        }

        using (var valid = Build(_ => { }))
            valid.GetService<IBackgroundExtraction>().Should().NotBeNull();

        using var invalid = Build(o => o.BackgroundExtractionConcurrency = 0);
        var read = () => invalid.GetRequiredService<IOptions<AgentFrameworkOptions>>().Value;
        read.Should().Throw<OptionsValidationException>().WithMessage("*BackgroundExtractionConcurrency*");
    }
}
