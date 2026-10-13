using AgentMemory.Abstractions.Domain;
using System.Threading.Channels;
using System.Text.Json.Serialization;

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
    public ExtractionPath ExtractionPath { get; init; }
    /// <summary>Absent for baseline receipts. Journal means this receipt was persisted; uncertain requires recovery.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Durability { get; init; }
}

/// <summary>Bounded single-writer queue with optional host-owned durable receipts.</summary>
internal sealed class CodingWriteQueue : BackgroundService
{
    private readonly ICodingWriter _writer;
    private readonly CodingContentPolicy _content;
    private readonly Channel<Entry> _channel;
    private readonly int _retainedJobs;
    private readonly int _capacity;
    private readonly ICodingWriteJournal? _journal;
    private bool _journalFailed;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _turns = new(StringComparer.Ordinal);
    private readonly Queue<string> _completed = new();
    private bool _accepting = true;

    public CodingWriteQueue(ICodingWriter writer, CodingContentPolicy content, int capacity = 64, int retainedJobs = 512,
        ICodingWriteJournal? journal = null)
    {
        if (capacity is < 1 or > 256 || retainedJobs < capacity || retainedJobs > 4096)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Queue capacity must be 1..256; retention must be capacity..4096.");
        _writer = writer;
        _content = content;
        _retainedJobs = retainedJobs;
        _capacity = capacity;
        _journal = journal;
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        if (_journal is not null) Recover();
    }

    public CodingWriteJob? TryEnqueue(CodingIdentity identity, CodingRememberRequest request)
    {
        _content.Check(request);
        if (_journal is not null && !_journal.CanResume(identity)) throw new ArgumentException("Write is not authorized by the journal host.");
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
                if (_journal is not null)
                {
                    try { _journal.Remove(expired); }
                    catch { JournalFailed(); throw; }
                }
                _jobs.Remove(expired);
                _turns.Remove(old.Key);
            }
            if (_jobs.Count >= _retainedJobs || (_journal is not null && _channel.Reader.Count >= _capacity)) return null;
            var job = new CodingWriteJob
            {
                JobId = Guid.NewGuid().ToString("N"), State = CodingJobState.Queued, QueuedAtUtc = DateTimeOffset.UtcNow,
                Durability = _journal is null ? null : "journal",
            };
            var entry = new Entry(identity, key, hash, request, job);
            if (_journal is not null)
            {
                try { _journal.Save(Snapshot(entry)); }
                catch { JournalFailed(); throw; }
            }
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
                    var running = entry.Job with { State = CodingJobState.Running };
                    if (_journal is not null)
                    {
                        try { _journal.Save(Snapshot(entry) with { Job = running }); }
                        catch { JournalFailed(entry); break; }
                    }
                    entry.Job = running;
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
                    Finish(entry, state, result.SourceMessageId, error,
                        result.PriorAttemptUnknown ? ExtractionPath.Unknown : result.ExtractionPath);
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
        // Durable never-started work remains queued for recovery, including orderly shutdown.
        if (_journal is not null) return;
        lock (_gate)
        {
            foreach (var entry in _jobs.Values.Where(e => e.Job.State == CodingJobState.Queued))
                Finish(entry, CodingJobState.Cancelled, null, "shutdown_before_write");
        }
    }

    private void Finish(Entry entry, CodingJobState state, string? source, string? error,
        ExtractionPath path = ExtractionPath.Unknown)
    {
        lock (_gate)
        {
            var finished = entry.Job with
            {
                State = state, SourceMessageId = source, ErrorCode = error, CompletedAtUtc = DateTimeOffset.UtcNow,
                ExtractionPath = path,
            };
            if (_journal is not null)
            {
                if (_journalFailed) { JournalFailed(entry); return; }
                try { _journal.Save(Snapshot(entry) with { Job = finished, Request = null }); }
                catch { JournalFailed(entry); return; }
            }
            entry.Job = finished;
            entry.Request = null;
            _completed.Enqueue(entry.Job.JobId);
        }
    }

    private void Recover()
    {
        var saved = _journal!.Load();
        if (saved.Count > _retainedJobs || saved.Count(e => e.Job.State == CodingJobState.Queued) > _capacity)
            throw new InvalidOperationException("Journal exceeds the configured queue bounds.");
        foreach (var record in saved)
        {
            var entry = new Entry(record.Identity, record.Key, record.Hash, record.Request, record.Job);
            if (!_jobs.TryAdd(entry.Job.JobId, entry) || !_turns.TryAdd(entry.Key, entry.Job.JobId))
                throw new InvalidOperationException("Journal contains conflicting receipt identities.");
            if (entry.Job.State == CodingJobState.Running)
                Finish(entry, CodingJobState.Unknown, null, "restart_during_write");
            else if (entry.Job.State == CodingJobState.Queued)
            {
                if (!_journal.CanResume(entry.Identity)) Finish(entry, CodingJobState.Cancelled, null, "recovery_not_authorized");
                else
                {
                    try
                    {
                        if (entry.Request is null || entry.Hash != entry.Request.PayloadHash() || entry.Key !=
                            CodingInput.MessageId(CodingInput.SessionId(entry.Identity, entry.Request.Session), entry.Request.TurnId, "user"))
                            throw new InvalidOperationException("Journal request does not match its receipt.");
                        _content.Check(entry.Request);
                    }
                    catch (ArgumentException) { Finish(entry, CodingJobState.Cancelled, null, "recovery_input_rejected"); }
                    if (entry.Job.State == CodingJobState.Queued && !_channel.Writer.TryWrite(entry))
                        throw new InvalidOperationException("Journal recovery exceeded queue capacity.");
                }
            }
            else _completed.Enqueue(entry.Job.JobId);
            if (_journalFailed) throw new IOException("Journal recovery could not persist a receipt.");
        }
    }

    private void JournalFailed(Entry? entry = null)
    {
        _journalFailed = true;
        StopAccepting();
        if (entry is not null)
        {
            entry.Job = entry.Job with
            {
                State = CodingJobState.Unknown, ErrorCode = "journal_unavailable", Durability = "uncertain",
                CompletedAtUtc = DateTimeOffset.UtcNow, ExtractionPath = ExtractionPath.Unknown,
            };
            entry.Request = null;
        }
    }

    private static CodingJournalEntry Snapshot(Entry entry) => new()
    {
        Identity = entry.Identity, Key = entry.Key, Hash = entry.Hash, Request = entry.Request, Job = entry.Job,
    };

    private sealed class Entry(CodingIdentity identity, string key, string hash, CodingRememberRequest? request, CodingWriteJob job)
    {
        public CodingIdentity Identity { get; } = identity;
        public string Key { get; } = key;
        public string Hash { get; } = hash;
        public CodingRememberRequest? Request { get; set; } = request;
        public CodingWriteJob Job { get; set; } = job;
    }
}
