using AgentMemory.Abstractions.Domain;
using System.Threading.Channels;

namespace AgentMemory.McpHost.Coding;

internal enum CodingJobState { Queued, Running, Succeeded, Partial, Failed, Unknown, Cancelled }

internal sealed record CodingWriteJob
{
    public required string JobId { get; init; }
    public required CodingJobState State { get; init; }
    public required DateTimeOffset QueuedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? SourceMessageId { get; init; }
    public string? ErrorCode { get; init; }
}

/// <summary>Bounded in-process write queue; one worker preserves turn order and owns no HTTP request scope.</summary>
internal sealed class CodingWriteQueue : BackgroundService
{
    private readonly ICodingWriter _writer;
    private readonly CodingContentPolicy _content;
    private readonly Channel<Entry> _channel;
    private readonly int _retainedJobs;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _turns = new(StringComparer.Ordinal);
    private readonly Queue<string> _completed = new();
    private bool _accepting = true;

    public CodingWriteQueue(ICodingWriter writer, CodingContentPolicy content, int capacity = 64, int retainedJobs = 512)
    {
        if (capacity is < 1 or > 256 || retainedJobs < capacity || retainedJobs > 4096)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Queue capacity must be 1..256; retention must be capacity..4096.");
        _writer = writer;
        _content = content;
        _retainedJobs = retainedJobs;
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public CodingWriteJob? TryEnqueue(CodingIdentity identity, CodingRememberRequest request)
    {
        _content.Check(request);
        var key = CodingInput.MessageId(CodingInput.SessionId(identity, request.Session), request.TurnId, "user");
        var hash = request.PayloadHash();
        lock (_gate)
        {
            if (_turns.TryGetValue(key, out var previous))
            {
                var existing = _jobs[previous];
                if (existing.Hash != hash) throw new CodingTurnConflictException();
                return existing.Job;
            }
            if (!_accepting) return null;
            while (_jobs.Count >= _retainedJobs && _completed.TryDequeue(out var expired))
            {
                var old = _jobs[expired];
                _jobs.Remove(expired);
                _turns.Remove(old.Key);
            }
            if (_jobs.Count >= _retainedJobs) return null;
            var job = new CodingWriteJob
            {
                JobId = Guid.NewGuid().ToString("N"), State = CodingJobState.Queued, QueuedAtUtc = DateTimeOffset.UtcNow,
            };
            var entry = new Entry(identity, key, hash, request, job);
            if (!_channel.Writer.TryWrite(entry)) return null;
            _jobs.Add(job.JobId, entry);
            _turns.Add(key, job.JobId);
            return job;
        }
    }

    public CodingWriteJob? Get(CodingIdentity identity, string jobId)
    {
        lock (_gate) return _jobs.TryGetValue(jobId, out var entry) &&
            entry.Identity.OwnerId == identity.OwnerId && entry.Identity.ProjectId == identity.ProjectId ? entry.Job : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    // ReadAllAsync can yield buffered entries even after cancellation was requested.
                    if (!_accepting || stoppingToken.IsCancellationRequested) break;
                    entry.Job = entry.Job with { State = CodingJobState.Running };
                }
                try
                {
                    var result = await _writer.WriteAsync(entry.Identity, entry.Request!, stoppingToken).ConfigureAwait(false);
                    var state = result.PriorAttemptUnknown ? CodingJobState.Unknown : result.Status switch
                    {
                        IngestionStatus.Succeeded => CodingJobState.Succeeded,
                        IngestionStatus.PartiallySucceeded => CodingJobState.Partial,
                        IngestionStatus.Failed => CodingJobState.Failed,
                        _ => CodingJobState.Unknown,
                    };
                    var error = state switch
                    {
                        CodingJobState.Partial => "partial_ingestion",
                        CodingJobState.Failed => "ingestion_failed",
                        CodingJobState.Unknown => "prior_attempt_unknown",
                        _ => null,
                    };
                    Finish(entry, state, result.SourceMessageId, error);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Finish(entry, CodingJobState.Unknown, null, "shutdown_during_write");
                }
                catch (Exception)
                {
                    // Provider exceptions may contain request text or credentials. Retain only a stable code.
                    Finish(entry, CodingJobState.Failed, null, "writer_failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            StopAccepting();
            CancelPending();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        StopAccepting();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        CancelPending();
    }

    public override void Dispose()
    {
        StopAccepting();
        base.Dispose();
        CancelPending();
    }

    private void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
            _channel.Writer.TryComplete();
        }
    }

    private void CancelPending()
    {
        lock (_gate)
        {
            foreach (var entry in _jobs.Values.Where(e => e.Job.State == CodingJobState.Queued))
                Finish(entry, CodingJobState.Cancelled, null, "shutdown_before_write");
        }
    }

    private void Finish(Entry entry, CodingJobState state, string? source, string? error)
    {
        lock (_gate)
        {
            entry.Job = entry.Job with { State = state, SourceMessageId = source, ErrorCode = error, CompletedAtUtc = DateTimeOffset.UtcNow };
            entry.Request = null;
            _completed.Enqueue(entry.Job.JobId);
        }
    }

    private sealed class Entry(CodingIdentity identity, string key, string hash, CodingRememberRequest request, CodingWriteJob job)
    {
        public CodingIdentity Identity { get; } = identity;
        public string Key { get; } = key;
        public string Hash { get; } = hash;
        public CodingRememberRequest? Request { get; set; } = request;
        public CodingWriteJob Job { get; set; } = job;
    }
}
