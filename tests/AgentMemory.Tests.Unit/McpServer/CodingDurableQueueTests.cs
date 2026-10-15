using AgentMemory.Abstractions.Domain;
using AgentMemory.McpHost.Coding;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingDurableQueueTests
{
    private static readonly CodingIdentity Identity = new("owner", "alpha");
    private static CodingRememberRequest Request(string turn = "turn") => new()
    {
        Session = "session", TurnId = turn, UserText = "Use cobalt.", Commit = new string('a', 40), Source = "synthetic",
    };

    [Fact]
    public void Acceptance_is_persisted_before_returning_a_receipt()
    {
        var journal = new Journal();
        using var queue = new CodingWriteQueue(new Writer(), new([]), journal: journal);
        var job = queue.TryEnqueue(Identity, Request())!;
        journal.Entries.Should().ContainKey(job.JobId);
        journal.Entries[job.JobId].Request.Should().Be(Request());
    }

    [Fact]
    public async Task Restart_recovers_never_started_work_but_never_replays_a_started_attempt()
    {
        var journal = new Journal();
        var waiting = Seed(journal, "waiting", CodingJobState.Queued);
        var started = Seed(journal, "started", CodingJobState.Running);
        var writer = new Writer();
        using var queue = new CodingWriteQueue(writer, new([]), journal: journal);
        queue.Get(Identity, started.JobId)!.State.Should().Be(CodingJobState.Unknown);
        queue.Get(Identity, started.JobId)!.ErrorCode.Should().Be("restart_during_write");
        journal.Entries[started.JobId].Request.Should().BeNull();
        await queue.StartAsync(default);
        try
        {
            var result = await Finished(queue, waiting.JobId);
            result.State.Should().Be(CodingJobState.Succeeded);
            writer.Turns.Should().Equal("waiting");
            journal.Entries[waiting.JobId].Job.Should().Be(result);
            journal.Entries[waiting.JobId].Request.Should().BeNull();
        }
        finally { await queue.StopAsync(default); }
    }

    [Fact]
    public void Retained_receipts_deduplicate_and_detect_changed_payload_after_restart()
    {
        var journal = new Journal();
        var original = Seed(journal, "turn", CodingJobState.Succeeded);
        using var queue = new CodingWriteQueue(new Writer(), new([]), journal: journal);
        queue.TryEnqueue(Identity, Request())!.JobId.Should().Be(original.JobId);
        var conflict = () => queue.TryEnqueue(Identity, Request() with { UserText = "Use amber." });
        conflict.Should().Throw<CodingTurnConflictException>();
        queue.Get(Identity with { ProjectId = "beta" }, original.JobId).Should().BeNull();
    }

    [Fact]
    public async Task Revoked_host_write_permission_cancels_recovery_without_writing()
    {
        var journal = new Journal { AllowResume = false };
        var waiting = Seed(journal, "turn", CodingJobState.Queued);
        var writer = new Writer();
        using var queue = new CodingWriteQueue(writer, new([]), journal: journal);
        queue.Get(Identity, waiting.JobId)!.State.Should().Be(CodingJobState.Cancelled);
        queue.Get(Identity, waiting.JobId)!.ErrorCode.Should().Be("recovery_not_authorized");
        await queue.StartAsync(default);
        await queue.StopAsync(default);
        writer.Turns.Should().BeEmpty();
    }

    [Fact]
    public void Failed_acceptance_never_returns_a_durable_receipt_or_executes_work()
    {
        var journal = new Journal { FailSaveAt = 1 };
        var writer = new Writer();
        using var queue = new CodingWriteQueue(writer, new([]), journal: journal);
        queue.Invoking(q => q.TryEnqueue(Identity, Request())).Should().Throw<IOException>();
        queue.TryEnqueue(Identity, Request("later")).Should().BeNull();
        journal.Entries.Should().BeEmpty();
        writer.Turns.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    public async Task Journal_failure_before_start_or_after_write_never_claims_success(int failAt, int writes)
    {
        var journal = new Journal { FailSaveAt = failAt };
        var writer = new Writer();
        using var queue = new CodingWriteQueue(writer, new([]), journal: journal);
        var job = queue.TryEnqueue(Identity, Request())!;
        await queue.StartAsync(default);
        try
        {
            var final = await Finished(queue, job.JobId);
            final.State.Should().Be(CodingJobState.Unknown);
            final.Durability.Should().Be("uncertain");
            final.ErrorCode.Should().Be("journal_unavailable");
            writer.Turns.Should().HaveCount(writes);
            queue.TryEnqueue(Identity, Request("later")).Should().BeNull();
            journal.Entries[job.JobId].Job.State.Should().Be(writes == 0 ? CodingJobState.Queued : CodingJobState.Running);
        }
        finally { await queue.StopAsync(default); }
    }

    [Fact]
    public async Task Durable_orderly_shutdown_retains_waiting_work_for_the_next_host()
    {
        var journal = new Journal();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new Writer(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        CodingWriteJob waiting;
        using (var queue = new CodingWriteQueue(writer, new([]), journal: journal))
        {
            var first = queue.TryEnqueue(Identity, Request("running"))!;
            waiting = queue.TryEnqueue(Identity, Request("waiting"))!;
            await queue.StartAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.StopAsync(default);
            queue.Get(Identity, first.JobId)!.State.Should().Be(CodingJobState.Unknown);
            queue.Get(Identity, waiting.JobId)!.State.Should().Be(CodingJobState.Queued);
        }
        var resumed = new Writer();
        using var restarted = new CodingWriteQueue(resumed, new([]), journal: journal);
        await restarted.StartAsync(default);
        try { (await Finished(restarted, waiting.JobId)).State.Should().Be(CodingJobState.Succeeded); }
        finally { await restarted.StopAsync(default); }
        resumed.Turns.Should().Equal("waiting");
    }

    [Fact]
    public void Full_queue_does_not_persist_an_unaccepted_extra_turn()
    {
        var journal = new Journal();
        using var queue = new CodingWriteQueue(new Writer(), new([]), capacity: 1, retainedJobs: 2, journal: journal);
        var job = queue.TryEnqueue(Identity, Request())!;
        queue.TryEnqueue(Identity, Request("overflow")).Should().BeNull();
        journal.Entries.Keys.Should().Equal(job.JobId);
    }

    [Fact]
    public async Task Retention_evicts_only_terminal_receipts_and_stays_bounded_on_disk()
    {
        var journal = new Journal();
        using var queue = new CodingWriteQueue(new Writer(), new([]), capacity: 1, retainedJobs: 2, journal: journal);
        await queue.StartAsync(default);
        try
        {
            var first = queue.TryEnqueue(Identity, Request("one"))!;
            await Finished(queue, first.JobId);
            var second = queue.TryEnqueue(Identity, Request("two"))!;
            await Finished(queue, second.JobId);
            var third = queue.TryEnqueue(Identity, Request("three"))!;
            await Finished(queue, third.JobId);
            journal.Entries.Keys.Should().BeEquivalentTo(new[] { second.JobId, third.JobId });
            queue.Get(Identity, first.JobId).Should().BeNull();
        }
        finally { await queue.StopAsync(default); }
    }

    [Fact]
    public void Changed_content_policy_cancels_queued_recovery_without_releasing_captured_text()
    {
        var journal = new Journal();
        var job = Seed(journal, "turn", CodingJobState.Queued);
        using var queue = new CodingWriteQueue(new Writer(), new(["cobalt"]), journal: journal);
        queue.Get(Identity, job.JobId)!.State.Should().Be(CodingJobState.Cancelled);
        queue.Get(Identity, job.JobId)!.ErrorCode.Should().Be("recovery_input_rejected");
        journal.Entries[job.JobId].Request.Should().BeNull();
    }

    private static CodingWriteJob Seed(Journal journal, string turn, CodingJobState state)
    {
        var request = Request(turn);
        var job = new CodingWriteJob { JobId = Guid.NewGuid().ToString("N"), State = state, QueuedAtUtc = DateTimeOffset.UtcNow, Durability = "journal" };
        journal.Entries.Add(job.JobId, new CodingJournalEntry
        {
            Identity = Identity, Key = CodingInput.MessageId(CodingInput.SessionId(Identity, request.Session), turn, "user"),
            Hash = request.PayloadHash(), Request = state is CodingJobState.Queued or CodingJobState.Running ? request : null, Job = job,
        });
        return job;
    }

    private static async Task<CodingWriteJob> Finished(CodingWriteQueue queue, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var job = queue.Get(Identity, id)!;
            if (job.State is not (CodingJobState.Queued or CodingJobState.Running)) return job;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class Journal : ICodingWriteJournal
    {
        public Dictionary<string, CodingJournalEntry> Entries { get; } = [];
        public bool AllowResume { get; init; } = true;
        public IReadOnlyList<CodingJournalEntry> Load() => Entries.Values.ToArray();
        public int FailSaveAt { get; init; }
        private int _saves;
        public void Save(CodingJournalEntry entry)
        {
            if (++_saves == FailSaveAt) throw new IOException("synthetic persistence failure");
            Entries[entry.Job.JobId] = entry;
        }
        public void Remove(string jobId) => Entries.Remove(jobId);
        public bool CanResume(CodingIdentity identity) => AllowResume && identity.CanWrite;
    }

    private sealed class Writer(Func<CodingRememberRequest, CancellationToken, Task>? work = null) : ICodingWriter
    {
        public List<string> Turns { get; } = [];
        public async Task<CodingWriteResult> WriteAsync(CodingIdentity identity, CodingRememberRequest request, CancellationToken cancellationToken = default)
        {
            Turns.Add(request.TurnId);
            if (work is not null) await work(request, cancellationToken);
            return new CodingWriteResult { SourceMessageId = "source", Status = IngestionStatus.Succeeded };
        }
    }
}
