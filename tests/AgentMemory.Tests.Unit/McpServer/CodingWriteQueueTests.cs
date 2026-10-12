using AgentMemory.Abstractions.Domain;
using AgentMemory.McpHost.Coding;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingWriteQueueTests
{
    private static readonly CodingIdentity Alpha = new("owner", "alpha");
    private static CodingRememberRequest Request(string turn = "one") => new()
    {
        Session = "session", TurnId = turn, UserText = "Use port 5234.", Commit = new string('a', 40), Source = "settings.cs",
    };

    [Fact]
    public void Secret_content_is_rejected_before_queue_admission()
    {
        using var queue = new CodingWriteQueue(new TestWriter(), new(["canary-credential-value"]));
        var run = () => queue.TryEnqueue(Alpha, Request() with { UserText = "canary-credential-value" });
        run.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Capacity_duplicate_submissions_and_changed_payloads_are_checked_before_execution()
    {
        var writer = new TestWriter();
        using var queue = new CodingWriteQueue(writer, new([]), capacity: 1, retainedJobs: 2);
        var first = queue.TryEnqueue(Alpha, Request());
        first.Should().NotBeNull();
        first!.State.Should().Be(CodingJobState.Queued);
        queue.TryEnqueue(Alpha, Request()).Should().Be(first);
        queue.TryEnqueue(Alpha, Request("two")).Should().BeNull();
        var conflict = () => queue.TryEnqueue(Alpha, Request() with { UserText = "changed" });
        conflict.Should().Throw<InvalidOperationException>();
        writer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Jobs_keep_their_project_run_in_order_and_do_not_block_submission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<(CodingIdentity, string)>();
        var writer = new TestWriter(async (identity, request, ct) =>
            {
                calls.Add((identity, request.TurnId));
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new CodingWriteResult { SourceMessageId = "source", Status = IngestionStatus.Succeeded };
            });
        using var queue = new CodingWriteQueue(writer, new([]), 2, 4);
        await queue.StartAsync(default);
        try
        {
            var first = queue.TryEnqueue(Alpha, Request())!;
            first.Should().NotBeNull();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var beta = Alpha with { ProjectId = "beta" };
            var second = queue.TryEnqueue(beta, Request("two"))!;
            second.State.Should().Be(CodingJobState.Queued);
            queue.Get(beta, first.JobId).Should().BeNull();
            queue.Get(Alpha with { OwnerId = "other" }, first.JobId).Should().BeNull();
            queue.Get(Alpha, first.JobId)!.State.Should().Be(CodingJobState.Running);
            release.SetResult();
            await Finished(queue, beta, second.JobId);
            calls.Should().Equal((Alpha, "one"), (beta, "two"));
            queue.Get(Alpha, first.JobId)!.State.Should().Be(CodingJobState.Succeeded);
            queue.TryEnqueue(Alpha, Request())!.JobId.Should().Be(first.JobId);
        }
        finally { release.TrySetResult(); await queue.StopAsync(default); }
    }

    [Theory]
    [InlineData(IngestionStatus.PartiallySucceeded, false, "Partial")]
    [InlineData(IngestionStatus.Failed, false, "Failed")]
    [InlineData(null, true, "Unknown")]
    public async Task Result_status_is_preserved(IngestionStatus? status, bool unknown, string expected)
    {
        var writer = new TestWriter((_, _, _) => Task.FromResult(
            new CodingWriteResult { SourceMessageId = "source", Status = status, PriorAttemptUnknown = unknown }));
        using var queue = new CodingWriteQueue(writer, new([]));
        await queue.StartAsync(default);
        try
        {
            var job = queue.TryEnqueue(Alpha, Request())!;
            job.Should().NotBeNull();
            var final = await Finished(queue, Alpha, job.JobId);
            final.State.ToString().Should().Be(expected);
            final.SourceMessageId.Should().Be("source");
        }
        finally { await queue.StopAsync(default); }
    }

    [Fact]
    public async Task Failure_does_not_leak_payload_or_provider_exception_and_does_not_stop_the_worker()
    {
        var writer = new TestWriter((_, request, _) => request.TurnId == "one"
                ? Task.FromException<CodingWriteResult>(new InvalidOperationException("private body and credential"))
                : Task.FromResult(new CodingWriteResult { SourceMessageId = "source", Status = IngestionStatus.Succeeded }));
        using var queue = new CodingWriteQueue(writer, new([]));
        await queue.StartAsync(default);
        try
        {
            var first = queue.TryEnqueue(Alpha, Request())!;
            first.Should().NotBeNull();
            var second = queue.TryEnqueue(Alpha, Request("two"))!;
            var failed = await Finished(queue, Alpha, first.JobId);
            failed.State.Should().Be(CodingJobState.Failed);
            failed.ErrorCode.Should().Be("writer_failed");
            failed.ToString().Should().NotContain("private body");
            (await Finished(queue, Alpha, second.JobId)).State.Should().Be(CodingJobState.Succeeded);
        }
        finally { await queue.StopAsync(default); }
    }

    [Fact]
    public async Task Shutdown_cancels_waiting_jobs_and_marks_an_interrupted_write_unknown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new TestWriter(async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new CodingWriteResult { SourceMessageId = "source", Status = IngestionStatus.Succeeded };
        });
        using var queue = new CodingWriteQueue(writer, new([]), 2, 4);
        await queue.StartAsync(default);
        var first = queue.TryEnqueue(Alpha, Request())!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = queue.TryEnqueue(Alpha, Request("two"))!;
        await queue.StopAsync(default);
        writer.Calls.Should().Be(1);
        queue.Get(Alpha, first.JobId)!.State.Should().Be(CodingJobState.Unknown);
        queue.Get(Alpha, waiting.JobId)!.State.Should().Be(CodingJobState.Cancelled);
        queue.TryEnqueue(Alpha, Request("three")).Should().BeNull();
    }

    [Fact]
    public void Disposal_before_start_closes_admission_and_releases_waiting_jobs()
    {
        var queue = new CodingWriteQueue(new TestWriter(), new([]));
        var waiting = queue.TryEnqueue(Alpha, Request())!;
        queue.Dispose();
        queue.Get(Alpha, waiting.JobId)!.State.Should().Be(CodingJobState.Cancelled);
        queue.TryEnqueue(Alpha, Request("two")).Should().BeNull();
    }

    [Fact]
    public async Task Completed_job_retention_is_bounded_without_evicting_active_work()
    {
        using var queue = new CodingWriteQueue(new TestWriter(), new([]), 1, 2);
        await queue.StartAsync(default);
        try
        {
            var first = queue.TryEnqueue(Alpha, Request())!;
            await Finished(queue, Alpha, first.JobId);
            var second = queue.TryEnqueue(Alpha, Request("two"))!;
            await Finished(queue, Alpha, second.JobId);
            var third = queue.TryEnqueue(Alpha, Request("three"))!;
            await Finished(queue, Alpha, third.JobId);
            queue.Get(Alpha, first.JobId).Should().BeNull();
            queue.Get(Alpha, second.JobId).Should().NotBeNull();
            queue.Get(Alpha, third.JobId).Should().NotBeNull();
        }
        finally { await queue.StopAsync(default); }
    }

    private static async Task<CodingWriteJob> Finished(CodingWriteQueue queue, CodingIdentity identity, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var job = queue.Get(identity, id)!;
            if (job.State is not (CodingJobState.Queued or CodingJobState.Running)) return job;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class TestWriter(Func<CodingIdentity, CodingRememberRequest, CancellationToken, Task<CodingWriteResult>>? write = null)
        : ICodingWriter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<CodingWriteResult> WriteAsync(CodingIdentity identity, CodingRememberRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return write?.Invoke(identity, request, cancellationToken) ?? Task.FromResult(
                new CodingWriteResult { SourceMessageId = "source", Status = IngestionStatus.Succeeded });
        }
    }
}
