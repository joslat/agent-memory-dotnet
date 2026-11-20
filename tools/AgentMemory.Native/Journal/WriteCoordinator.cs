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
    private long _deletionEpoch;
    private long _memoryRevision;

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

    /// <summary>How many owner erases have started (from the journal, so it survives restarts).</summary>
    public ulong DeletionEpoch => (ulong)Interlocked.Read(ref _deletionEpoch);

    /// <summary>How many operations have ended (any ending): it moves whenever the owner's memory may have changed.</summary>
    public ulong MemoryRevision => (ulong)Interlocked.Read(ref _memoryRevision);

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

    /// <summary>
    /// Accepts a forget: of everything one remember produced (<paramref name="targetOperationId"/>), or of the owner's
    /// facts and preferences by id. A remember that has not started yet is cancelled at once instead (it never runs).
    /// </summary>
    public Operation AdmitForget(string? targetOperationId, IReadOnlyList<string>? memoryIds)
    {
        Operation operation;
        lock (_journal.Gate)
        {
            if (targetOperationId is not null)
            {
                var target = _journal.FindLocked(targetOperationId)
                    ?? throw new ContractException("operation_not_found", $"No operation '{targetOperationId}' is in this journal.");
                if (target.Kind != OperationKind.Remember)
                    throw new ContractException("not_a_remember", $"Operation '{targetOperationId}' is not a remember; only what a remember produced can be forgotten by its id.");
            }
            operation = AcceptLocked(OperationKind.Forget, targetOperationId, memoryIds);
            if (targetOperationId is not null && _journal.FindLocked(targetOperationId) is { State: OperationState.Accepted } queued)
                _journal.AppendLocked(new JournalRecord
                {
                    Kind = "cancelled", OperationId = queued.OperationId, At = DateTimeOffset.UtcNow,
                    Reason = $"forgotten_before_it_ran: {operation.OperationId}",
                });
            Recount();
        }
        _stateChanged();
        return operation;
    }

    /// <summary>
    /// Accepts the erase of everything of the owner. Refused (nothing accepted) while any write is accepted or running:
    /// an erase runs only when nothing else can still write.
    /// </summary>
    public Operation AdmitErase()
    {
        Operation operation;
        lock (_journal.Gate)
        {
            if (_pending > 0)
                throw new JournalException("writes_pending",
                    $"{_pending} operation(s) are accepted or running; erase when they have ended. Nothing was accepted.");
            operation = AcceptLocked(OperationKind.Erase, null, null);
            Recount();
        }
        _stateChanged();
        return operation;
    }

    private Operation AcceptLocked(OperationKind kind, string? targetOperationId, IReadOnlyList<string>? memoryIds)
    {
        if (_stopping) throw new JournalException("closing", "The context is closing; nothing was accepted.");
        if (_pending >= _maxQueued)
            throw new JournalException("write_queue_full",
                $"{_pending} operations are already waiting (the limit is {_maxQueued}); nothing was accepted.");
        var operationId = "op-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        _journal.AppendLocked(new JournalRecord
        {
            Kind = "accepted", OperationId = operationId, At = now, SourceId = "op:" + operationId, SourceSha256 = "",
            Owner = _owner, SessionId = "", OccurredAt = now, Op = kind == OperationKind.Forget ? "forget" : "erase",
            TargetOperationId = targetOperationId, MemoryIds = memoryIds is null ? null : [.. memoryIds],
        });
        var operation = _journal.FindLocked(operationId)!;
        _queue.Writer.TryWrite(operation);
        return operation;
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

    private Task<JournalRecord> RunAsync(Operation operation) => operation.Kind switch
    {
        OperationKind.Forget => RunForgetAsync(operation),
        OperationKind.Erase => RunEraseAsync(operation),
        _ => RunRememberAsync(operation),
    };

    private static string MessageId(Operation remember) => "msg-" + remember.OperationId["op-".Length..];

    private async Task<JournalRecord> RunRememberAsync(Operation operation)
    {
        var messageId = "msg-" + operation.OperationId["op-".Length..];
        var input = new RememberInput
        {
            Owner = operation.Owner,
            SessionId = operation.SessionId,
            ConversationId = ConversationId(operation.Owner, operation.SessionId),
            MessageId = messageId,
            Text = operation.Text ?? throw new InvalidOperationException("A remember without its words cannot run."),
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

    /// <summary>
    /// Closes what is to be forgotten, then checks the store: every closed id must read back closed. A remember forgotten
    /// as a whole is marked forgotten. A store failure while closing is unknown (a transaction may still commit).
    /// </summary>
    private async Task<JournalRecord> RunForgetAsync(Operation operation)
    {
        Operation? target = null;
        IReadOnlyList<string> ids;
        if (operation.TargetOperationId is { } targetId)
        {
            lock (_journal.Gate) target = _journal.FindLocked(targetId);
            if (target is null || target.State is OperationState.Cancelled or OperationState.Erased)
                return End(operation, "applied", target is null ? "nothing_to_forget" : $"nothing_to_forget: {Name(target.State)}");
            WrittenEvidence evidence;
            try
            {
                evidence = await WriteComposition.EvidenceAsync(_provider, _settings, MessageId(target), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return End(operation, "unknown", $"evidence_unreadable: {exception.GetType().Name}");
            }
            ids = [.. evidence.Items.Where(i => i.Live && i.Owner == _owner).Select(i => i.Id)];
        }
        else
        {
            ids = operation.MemoryIds;
        }

        IReadOnlyList<string> closed, missing;
        try
        {
            (closed, missing) = await WriteComposition.ForgetAsync(_provider, _owner, ids, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return End(operation, IsStoreFailure(exception) ? "unknown" : "partial", $"failed_while_closing: {exception.GetType().Name}");
        }

        IReadOnlyList<WrittenItem> after;
        try
        {
            after = await WriteComposition.ItemsAsync(_provider, _settings, closed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return End(operation, "unknown", $"evidence_unreadable: {exception.GetType().Name}");
        }
        var stillLive = after.Where(i => i.Live).Select(i => i.Id).ToList();
        var end = new JournalRecord
        {
            Kind = stillLive.Count == 0 ? "applied" : "partial",
            OperationId = operation.OperationId,
            At = DateTimeOffset.UtcNow,
            Reason = stillLive.Count > 0 ? $"still_live: {string.Join(",", stillLive)}"
                : missing.Count > 0 ? $"not_found: {string.Join(",", missing)}"
                : closed.Count == 0 ? "nothing_to_forget" : null,
            Closed = [.. closed],
        };
        if (target is not null && stillLive.Count == 0)
        {
            lock (_journal.Gate)
            {
                // The remember is ended as forgotten in the same breath, so its receipt never says applied again.
                if (target.State is not (OperationState.Cancelled or OperationState.Erased or OperationState.Forgotten))
                    _journal.AppendLocked(new JournalRecord
                    {
                        Kind = "forgotten", OperationId = target.OperationId, At = DateTimeOffset.UtcNow,
                        Reason = $"forgotten_by: {operation.OperationId}", Searchable = false,
                    });
            }
        }
        return end;
    }

    /// <summary>
    /// Erases everything of the owner, checks that nothing is left, ends every earlier write as erased, and rewrites the
    /// journal without any remembered words (always, even when the store part failed: the words are not needed again).
    /// </summary>
    private async Task<JournalRecord> RunEraseAsync(Operation operation)
    {
        List<string> messageIds;
        lock (_journal.Gate)
            messageIds = [.. _journal.OperationsLocked.Where(o => o.Kind == OperationKind.Remember).Select(MessageId)];

        JournalRecord end;
        long deleted = 0;
        try
        {
            deleted = await WriteComposition.EraseOwnerAsync(_provider, _owner, CancellationToken.None).ConfigureAwait(false);
            var remaining = await WriteComposition.RemainingAsync(_provider, _settings, _owner, messageIds, CancellationToken.None)
                .ConfigureAwait(false);
            end = remaining == 0
                ? new JournalRecord { Kind = "applied", OperationId = operation.OperationId, At = DateTimeOffset.UtcNow, Count = deleted }
                : new JournalRecord
                {
                    Kind = "partial", OperationId = operation.OperationId, At = DateTimeOffset.UtcNow, Count = deleted,
                    Reason = $"remaining: {remaining}",
                };
        }
        catch (Exception exception)
        {
            end = new JournalRecord
            {
                Kind = IsStoreFailure(exception) ? "unknown" : "partial", OperationId = operation.OperationId,
                At = DateTimeOffset.UtcNow, Count = deleted, Reason = $"failed_while_erasing: {exception.GetType().Name}",
            };
        }

        lock (_journal.Gate)
        {
            if (end.Kind == "applied")
            {
                // Everything earlier is gone with the owner: remembers end as erased, and an uncertain forget or erase is
                // settled by this one, so recall can open again.
                foreach (var earlier in _journal.OperationsLocked.Where(o => o != operation).ToList())
                {
                    var erased = earlier.Kind == OperationKind.Remember
                        ? earlier.State is not (OperationState.Cancelled or OperationState.Erased)
                        : earlier.State is OperationState.Partial or OperationState.Unknown;
                    if (erased)
                        _journal.AppendLocked(new JournalRecord
                        {
                            Kind = "erased", OperationId = earlier.OperationId, At = DateTimeOffset.UtcNow,
                            Reason = $"erased_by: {operation.OperationId}", Searchable = false,
                        });
                }
            }
            _journal.PurgeTextsLocked();
        }
        return end;
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
        OperationState.Kept => "kept",
        OperationState.Cancelled => "cancelled",
        OperationState.Forgotten => "forgotten",
        _ => "erased",
    };

    /// <summary>Recounts open and pending operations. Call under the journal's lock.</summary>
    private void Recount()
    {
        int open = 0, pending = 0;
        long erases = 0, ended = 0;
        foreach (var operation in _journal.OperationsLocked)
        {
            if (operation.IsOpen) open++;
            if (operation.IsPending) pending++;
            if (operation.Kind == OperationKind.Erase && operation.StartedAt is not null) erases++;
            if (!operation.IsPending) ended++;
        }
        Volatile.Write(ref _open, open);
        Volatile.Write(ref _pending, pending);
        Interlocked.Exchange(ref _deletionEpoch, erases);
        Interlocked.Exchange(ref _memoryRevision, ended);
    }
}
