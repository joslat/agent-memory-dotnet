using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.McpHost.Coding;

/// <summary>Content-free read-time observations; bounded process memory, never authority to retrieve content.</summary>
internal sealed class CodingRecallReceipts
{
    private readonly CodingRecallDiagnosticsOptions _options;
    private readonly CodingContentPolicy _content;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public CodingRecallReceipts(CodingRecallDiagnosticsOptions options, CodingContentPolicy content, TimeProvider? clock = null)
    {
        options.Validate();
        _options = options; _content = content; _clock = clock ?? TimeProvider.System;
    }

    public CodingRecallReceipt Record(CodingIdentity identity, string session, RecallResult result, TimeSpan elapsed)
    {
        var scope = Scope(identity, session);
        var now = _clock.GetUtcNow();
        var receipt = CodingRecallReceipt.Create(result, now, TimeSpan.FromSeconds(_options.LifetimeSeconds), elapsed);
        lock (_gate)
        {
            Expire(now);
            while (_entries.Count >= _options.RetainedReceipts) _entries.Remove(_order.Dequeue());
            _entries.Add(receipt.RecallId, new(scope, receipt));
            _order.Enqueue(receipt.RecallId);
        }
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("coding.recall.receipt");
        activity?.SetTag("coding.recall.id", receipt.RecallId);
        activity?.SetTag("coding.recall.items", receipt.TotalItemsRetrieved);
        activity?.SetTag("coding.recall.elapsed_ms", receipt.ElapsedMilliseconds);
        activity?.SetTag("coding.recall.path", receipt.Gate.Outcome);
        return receipt;
    }

    public CodingRecallReceipt? Get(CodingIdentity identity, string session, string? recallId = null)
    {
        var scope = Scope(identity, session);
        if (recallId is not null) ValidateId(recallId);
        lock (_gate)
        {
            Expire(_clock.GetUtcNow());
            if (recallId is not null) return _entries.TryGetValue(recallId, out var entry) && entry.Scope == scope ? entry.Receipt : null;
            // Bounded scan avoids a second session index that could retain expired sessions indefinitely.
            return _order.Reverse().Select(id => _entries[id]).FirstOrDefault(e => e.Scope == scope)?.Receipt;
        }
    }

    public CodingRecallReceipt? Feedback(CodingIdentity identity, string session, string recallId, CodingRecallFeedback label)
    {
        if (!identity.CanWrite) throw new UnauthorizedAccessException();
        var scope = Scope(identity, session);
        ValidateId(recallId);
        if (!Enum.IsDefined(label)) throw new ArgumentException("Unknown feedback label.");
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Expire(now);
            if (!_entries.TryGetValue(recallId, out var entry) || entry.Scope != scope) return null;
            if (entry.Receipt.Feedback == label) return entry.Receipt;
            var receipt = entry.Receipt with { Feedback = label, FeedbackAtUtc = now };
            _entries[recallId] = entry with { Receipt = receipt };
            using var activity = AgentMemoryDiagnostics.Source.StartActivity("coding.recall.feedback");
            activity?.SetTag("coding.recall.id", receipt.RecallId);
            activity?.SetTag("coding.recall.feedback", label.ToString().ToLowerInvariant());
            return receipt;
        }
    }

    private string Scope(CodingIdentity identity, string session)
    {
        CodingInput.RequireText(session, 256, nameof(session));
        _content.CheckText(session);
        return CodingInput.SessionId(identity, session);
    }
    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid recall ID.");
    }
    private void Expire(DateTimeOffset now)
    {
        // Concurrent completions and clock corrections need not preserve expiration order.
        var count = _order.Count;
        for (var i = 0; i < count; i++)
        {
            var id = _order.Dequeue();
            if (_entries[id].Receipt.ExpiresAtUtc <= now) _entries.Remove(id);
            else _order.Enqueue(id);
        }
    }
    private sealed record Entry(string Scope, CodingRecallReceipt Receipt);
}
