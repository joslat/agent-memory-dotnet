using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Native.Composition;
using AgentMemory.Native.Contract;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Native.Journal;

/// <summary>
/// The single writer of one explicit-write context. Admission appends the source to the journal (durable before it
/// returns); a background loop runs accepted operations one at a time, in acceptance order, on the context's lifetime
/// rather than the caller's job, and records how each one ended. Every ending is decided from evidence:
/// <list type="bullet">
/// <item><b>applied</b>: the writer reported success and every fact and preference the remembered message produced is
/// live, owned by the owner and embedded with the store's dimensions;</item>
/// <item><b>rejected</b>: it failed, and the store shows that nothing recall reads came from the message;</item>
/// <item><b>partial</b>: something came from it, but not everything succeeded or verified;</item>
/// <item><b>unknown</b>: the store failed while writing (a transaction may still commit), or the evidence cannot be read.</item>
/// </list>
/// Nothing is ever run twice: a duplicate source returns the existing receipt, and an operation that was running when a
/// process ended stays unknown until an operator resolves it.
/// </summary>
internal sealed class WriteCoordinator
{
    private readonly OperationJournal _journal;
    private readonly ServiceProvider _provider;
    private readonly MemorySettings _settings;
    private readonly string _owner;
    private readonly int _maxQueued;
    private readonly Action _stateChanged;
    private readonly Channel<Operation> _queue =
        Channel.CreateUnbounded<Operation>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task _loop = Task.CompletedTask;
    private Operation? _current;
    private bool _stopping;
    private int _open;
    private int _pending;

    public WriteCoordinator(OperationJournal journal, ServiceProvider provider, MemorySettings settings, string owner,
        int maxQueued, Action stateChanged)
    {
        _journal = journal;
        _provider = provider;
        _settings = settings;
        _owner = owner;
        _maxQueued = maxQueued;
        _stateChanged = stateChanged;
        lock (_journal.Gate) Recount();
    }

    /// <summary>No operation is accepted, running, partial or unknown: recall may read the owner's memory.</summary>
    public bool ReadAllowed => Volatile.Read(ref _open) == 0;

    /// <summary>No operation is accepted or running: nothing can start writing.</summary>
    public bool WriteQuiescent => Volatile.Read(ref _pending) == 0;

    /// <summary>Queues what was accepted before (in acceptance order) and starts the writer.</summary>
    public void Start()
    {
        lock (_journal.Gate)
        {
            foreach (var operation in _journal.OperationsLocked.Where(o => o.State == OperationState.Accepted))
                _queue.Writer.TryWrite(operation);
        }
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>
    /// Accepts the owner's words durably, or returns the existing operation for the same source. Throws
    /// <see cref="ContractException"/> for a source id reused with another payload and <see cref="JournalException"/> when
    /// the journal cannot take it (nothing is accepted then).
    /// </summary>
    public (Operation Operation, bool Duplicate) Admit(string sourceId, string sessionId, string text, DateTimeOffset occurredAt)
    {
        var hash = OperationJournal.SourceHash(_owner, sessionId, sourceId, text, occurredAt);
        Operation operation;
        lock (_journal.Gate)
        {
            if (_journal.FindBySourceLocked(sourceId) is { } existing)
            {
                if (!string.Equals(existing.SourceSha256, hash, StringComparison.Ordinal))
                    throw new ContractException("source_conflict",
                        "This source id was accepted before with other words, session or time; a changed source needs a new id.");
                return (existing, true);
            }
            if (_stopping) throw new JournalException("closing", "The context is closing; nothing was accepted.");
            if (_pending >= _maxQueued)
                throw new JournalException("write_queue_full",
                    $"{_pending} writes are already waiting (the limit is {_maxQueued}); nothing was accepted.");
            var operationId = "op-" + Guid.NewGuid().ToString("N");
            _journal.AppendLocked(new JournalRecord
            {
                Kind = "accepted", OperationId = operationId, At = DateTimeOffset.UtcNow, SourceId = sourceId,
                SourceSha256 = hash, Owner = _owner, SessionId = sessionId, Text = text, OccurredAt = occurredAt,
            });
            operation = _journal.FindLocked(operationId)!;
            _queue.Writer.TryWrite(operation);
            Recount();
        }
        _stateChanged();
        return (operation, false);
    }

    public OperationSnapshot? Find(string? operationId, string? sourceId)
    {
        lock (_journal.Gate)
        {
            var operation = operationId is not null ? _journal.FindLocked(operationId) : _journal.FindBySourceLocked(sourceId!);
            return operation?.Snapshot();
        }
    }

    public OperationSnapshot Snapshot(Operation operation)
    {
        lock (_journal.Gate) return operation.Snapshot();
    }

    /// <summary>Ends a partial or unknown operation by keeping what it wrote, as it is (recorded as not verified).</summary>
    public OperationSnapshot Keep(string operationId)
    {
        OperationSnapshot snapshot;
        lock (_journal.Gate)
        {
            var operation = _journal.FindLocked(operationId)
                ?? throw new ContractException("operation_not_found", $"No operation '{operationId}' is in this journal.");
            if (operation.State is not (OperationState.Partial or OperationState.Unknown))
                throw new ContractException("not_resolvable",
                    $"Operation '{operationId}' is {Name(operation.State)}; only a partial or unknown operation is resolved.");
            _journal.AppendLocked(new JournalRecord
            {
                Kind = "kept", OperationId = operationId, At = DateTimeOffset.UtcNow,
                Reason = $"kept_after_{Name(operation.State)}",
            });
            Recount();
            snapshot = operation.Snapshot();
        }
        _stateChanged();
        return snapshot;
    }

    /// <summary>Stops admission and the queue; true when no write is running. Queued writes stay accepted in the journal.</summary>
    public bool Stop()
    {
        lock (_journal.Gate)
        {
            _stopping = true;
            _queue.Writer.TryComplete();
            return _current is null;
        }
    }

    /// <summary>Waits for the writer loop to end; call after <see cref="Stop"/> returned true.</summary>
    public void WaitStopped() => _loop.GetAwaiter().GetResult();

    private async Task LoopAsync()
    {
        await foreach (var operation in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_journal.Gate)
            {
                if (_stopping || operation.State != OperationState.Accepted) continue;
                try
                {
                    _journal.AppendLocked(new JournalRecord { Kind = "running", OperationId = operation.OperationId, At = DateTimeOffset.UtcNow });
                }
                catch (JournalException)
                {
                    // Not started, and nothing recorded: it stays accepted and runs after the next open.
                    continue;
                }
                _current = operation;
                Recount();
            }
            _stateChanged();

            JournalRecord end;
            try
            {
                end = await RunAsync(operation).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                end = End(operation, "unknown", $"internal: {exception.GetType().Name}");
            }
            lock (_journal.Gate)
            {
                try
                {
                    _journal.AppendLocked(end);
                }
                catch (JournalException)
                {
                    // The end could not be recorded: the journal still says running, which the next open reports as
                    // unknown. Here it is unknown already, so recall stays closed and nothing runs it again.
                    operation.State = OperationState.Unknown;
                    operation.Reason = "journal_unavailable_at_end";
                    operation.FinishedAt = DateTimeOffset.UtcNow;
                    operation.Ended.TrySetResult();
                }
                _current = null;
                Recount();
            }
            _stateChanged();
        }
    }

    private async Task<JournalRecord> RunAsync(Operation operation)
    {
        var messageId = "msg-" + operation.OperationId["op-".Length..];
        var input = new RememberInput
        {
            Owner = operation.Owner,
            SessionId = operation.SessionId,
            ConversationId = ConversationId(operation.Owner, operation.SessionId),
            MessageId = messageId,
            Text = operation.Text,
            OccurredAt = operation.OccurredAt,
        };

        ExtractionResult? result = null;
        Exception? failure = null;
        try
        {
            // Never the caller's token: the operation outlives the job that asked for it, and a write stopped half-way is
            // worse than one that finishes.
            result = (await WriteComposition.RememberAsync(_provider, input, CancellationToken.None).ConfigureAwait(false)).Extraction;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // A store failure while writing can leave a transaction that still commits: its effects cannot be bounded now.
        if (failure is not null && IsStoreFailure(failure))
            return End(operation, "unknown", $"store_failed_while_writing: {failure.GetType().Name}");

        WrittenEvidence evidence;
        try
        {
            evidence = await WriteComposition.EvidenceAsync(_provider, _settings, messageId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return End(operation, "unknown", $"evidence_unreadable: {exception.GetType().Name}");
        }

        if (failure is not null)
            return evidence.Any
                ? End(operation, "partial", $"failed_after_writing: {Reason(failure)}", evidence)
                : End(operation, "rejected", Reason(failure));
        if (result!.Status == IngestionStatus.Failed)
            return evidence.Any
                ? End(operation, "partial", "failed_after_writing: " + FirstOutcome(result), evidence)
                : End(operation, "rejected", FirstOutcome(result));
        if (result.Status == IngestionStatus.PartiallySucceeded)
            return End(operation, "partial", "some_items_failed: " + FirstOutcome(result), evidence);
        if (evidence.Problem(_owner, _settings.EmbeddingDimensions) is { } problem)
            return End(operation, "partial", "not_verified: " + problem, evidence);
        if (result.Facts.Count + result.Preferences.Count > 0 && evidence.Items.Count == 0)
            return End(operation, "partial", "not_verified: the writer reported memories the store does not link to the message", evidence);
        return End(operation, "applied", evidence.Items.Count == 0 ? "nothing_to_keep" : null, evidence, searchable: evidence.Items.Count > 0);
    }

    private static JournalRecord End(Operation operation, string kind, string? reason, WrittenEvidence? evidence = null,
        bool searchable = false) => new()
    {
        Kind = kind,
        OperationId = operation.OperationId,
        At = DateTimeOffset.UtcNow,
        Reason = reason,
        Facts = evidence is null ? null : [.. evidence.Items.Where(i => i.Kind == "fact").Select(i => i.Id)],
        Preferences = evidence is null ? null : [.. evidence.Items.Where(i => i.Kind == "preference").Select(i => i.Id)],
        Closed = evidence is null ? null : [.. evidence.Closed],
        Searchable = searchable,
    };

    private static bool IsStoreFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is global::Neo4j.Driver.Neo4jException or TimeoutException) return true;
        return false;
    }

    private static string Reason(Exception exception) => exception switch
    {
        HttpRequestException => "model_unavailable",
        FormatException => "model_gave_no_usable_reply",
        _ => $"internal: {exception.GetType().Name}",
    };

    private static string FirstOutcome(ExtractionResult result) =>
        result.Outcomes.FirstOrDefault(o => o.Status == IngestionItemStatus.Failed) is { } failed
            ? $"{failed.Kind} at {failed.Stage}: {failed.ErrorCode ?? "failed"}"
            : "failed";

    /// <summary>One conversation per owner and session, stable across restarts.</summary>
    internal static string ConversationId(string owner, string sessionId) =>
        "conv-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"agentmemory.conversation.v1\n{owner}\n{sessionId}")))[..32];

    internal static string Name(OperationState state) => state switch
    {
        OperationState.Accepted => "accepted",
        OperationState.Running => "running",
        OperationState.Applied => "applied",
        OperationState.Rejected => "rejected",
        OperationState.Partial => "partial",
        OperationState.Unknown => "unknown",
        _ => "kept",
    };

    /// <summary>Recounts open and pending operations. Call under the journal's lock.</summary>
    private void Recount()
    {
        int open = 0, pending = 0;
        foreach (var operation in _journal.OperationsLocked)
        {
            if (operation.IsOpen) open++;
            if (operation.IsPending) pending++;
        }
        Volatile.Write(ref _open, open);
        Volatile.Write(ref _pending, pending);
    }
}
