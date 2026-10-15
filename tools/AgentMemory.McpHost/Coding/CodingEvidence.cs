using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.McpHost.Coding;

/// <summary>Optional delivery of selected handles followed by bounded current-state evidence reads.</summary>
internal sealed class CodingEvidence
{
    private readonly CodingMemoryOperations _memory;
    private readonly CodingEvidenceReader _reader;
    private readonly CodingEvidenceOptions _options;
    private readonly CodingRecallReceipts _previews;
    private readonly TimeProvider _clock;

    public CodingEvidence(CodingMemoryOperations memory, CodingEvidenceReader reader, CodingEvidenceOptions options,
        CodingContentPolicy content, TimeProvider? clock = null)
    {
        options.Validate(); _memory = memory; _reader = reader; _options = options;
        _clock = clock ?? TimeProvider.System;
        _previews = new(new() { RetainedReceipts = options.RetainedPreviews, LifetimeSeconds = options.LifetimeSeconds }, content, clock);
    }

    public async Task<CodingEvidencePreview> PreviewAsync(CodingIdentity identity, string session, string query,
        CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.DeadlineMilliseconds), _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var result = await _memory.RecallAsync(identity, session, query, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            CodingEvidencePreview? preview = null;
            _previews.Record(identity, session, result, Stopwatch.GetElapsedTime(started), receipt =>
            {
                var items = new List<CodingEvidencePreviewItem>();
                var selected = new List<CodingRecallItem>();
                var other = new List<string>();
                if (receipt.HasWorkingMemory) other.Add("workingMemory");
                if (receipt.HasGraphRagContext) other.Add("graphRagContext");
                if (result.Context.ForgottenTopics.Count != 0) other.Add("forgottenTopics");
                if (result.Context.RelevantRelationships.Items.Count != 0) other.Add("relationships");
                preview = new(receipt.RecallId, receipt.ExpiresAtUtc, items, receipt.IdsTruncated,
                    result.Truncated, result.Context.LatencyBudgetExceeded, other);
                foreach (var item in receipt.Items)
                {
                    if (items.Count >= _options.MaxItems) { preview = preview with { Truncated = true }; break; }
                    var metadata = Metadata(result.Context, item);
                    items.Add(new(item.Section, item.ItemId, item.SourceMessageIds, metadata.Time, metadata.Bytes));
                    if (Bytes(preview) > _options.MaxBytes)
                    {
                        items.RemoveAt(items.Count - 1); preview = preview with { Truncated = true }; break;
                    }
                    selected.Add(item);
                }
                return selected;
            });
            return preview!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new CodingEvidenceDeadlineException(); }
    }

    public async Task<CodingEvidenceExpansion?> ExpandAsync(CodingIdentity identity, string session, string previewId,
        IReadOnlyList<CodingEvidenceSelection> requested, CancellationToken cancellationToken = default)
    {
        if (requested is null || requested.Count is < 1 || requested.Count > _options.MaxItems)
            throw new ArgumentException("Evidence selection exceeds the configured item bound.");
        foreach (var item in requested)
        {
            if (item is null) throw new ArgumentException("Invalid evidence selection.");
            CodingInput.RequireText(item.Section, 32, nameof(item.Section));
            CodingInput.RequireText(item.ItemId, 256, nameof(item.ItemId));
            if (item.SourceMessageId is not null) CodingInput.RequireText(item.SourceMessageId, 256, nameof(item.SourceMessageId));
        }
        CodingInput.RequireText(previewId, 32, nameof(previewId));
        var receipt = _previews.Get(identity, session, previewId);
        if (receipt is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.DeadlineMilliseconds), _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var items = new List<CodingEvidenceItem>();
            var response = new CodingEvidenceExpansion(previewId, DateTimeOffset.UtcNow, items, false);
            foreach (var request in requested)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var selected = receipt.Items.FirstOrDefault(i => i.Section == request.Section && i.ItemId == request.ItemId);
                var value = selected is null ? CodingEvidenceReader.Unavailable(request) :
                    await _reader.ReadAsync(identity, request, selected, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (!AppendBounded(response, items, value)) { response = response with { Truncated = true }; break; }
                if (items[^1].Truncated) response = response with { Truncated = true };
            }
            deadline.Token.ThrowIfCancellationRequested();
            return _previews.Get(identity, session, previewId) is null ? null : response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new CodingEvidenceDeadlineException(); }
    }

    private bool AppendBounded(CodingEvidenceExpansion response, List<CodingEvidenceItem> items, CodingEvidenceItem value)
    {
        var text = value.Text;
        if (text is not null && text.Length > _options.MaxBytes)
            value = value with { Text = Clip(text, _options.MaxBytes), Truncated = true };
        items.Add(value);
        if (Bytes(response) <= _options.MaxBytes) return true;
        if (text is null) { items.RemoveAt(items.Count - 1); return false; }
        var lo = 0; var hi = Math.Min(text.Length, _options.MaxBytes);
        items[^1] = value with { Text = "", Truncated = true };
        if (Bytes(response) > _options.MaxBytes) { items.RemoveAt(items.Count - 1); return false; }
        while (lo < hi)
        {
            var middle = lo + (hi - lo + 1) / 2;
            items[^1] = value with { Text = Clip(text, middle), Truncated = true };
            if (Bytes(response) <= _options.MaxBytes) lo = middle; else hi = middle - 1;
        }
        items[^1] = value with { Text = Clip(text, lo), Truncated = true };
        return true;
    }
    private static int Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, CodingApi.Json).Length;
    private static string Clip(string value, int length)
    {
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
    private static (DateTimeOffset? Time, int Bytes) Metadata(MemoryContext context, CodingRecallItem item)
    {
        object? value = item.Section switch
        {
            "recentMessages" => context.RecentMessages.Items.FirstOrDefault(m => m.MessageId == item.ItemId),
            "relevantMessages" => context.RelevantMessages.Items.FirstOrDefault(m => m.MessageId == item.ItemId),
            "relevantFacts" => context.RelevantFacts.Items.FirstOrDefault(f => f.FactId == item.ItemId),
            "dueFacts" => context.DueFacts.Items.FirstOrDefault(f => f.FactId == item.ItemId),
            "expiringFacts" => context.ExpiringFacts.Items.FirstOrDefault(f => f.FactId == item.ItemId),
            "relevantEntities" => context.RelevantEntities.Items.FirstOrDefault(e => e.EntityId == item.ItemId),
            "relevantPreferences" => context.RelevantPreferences.Items.FirstOrDefault(p => p.PreferenceId == item.ItemId),
            "similarTraces" => context.SimilarTraces.Items.FirstOrDefault(t => t.TraceId == item.ItemId),
            _ => null,
        };
        return value switch
        {
            Fact f => (f.CreatedAtUtc, Encoding.UTF8.GetByteCount($"{f.Subject} {f.Predicate} {f.Object}")),
            Entity e => (e.CreatedAtUtc, Encoding.UTF8.GetByteCount(e.Name + (e.Type is null ? "" : " (" + e.Type + ")"))),
            Preference p => (p.CreatedAtUtc, Encoding.UTF8.GetByteCount(p.Category + ": " + p.PreferenceText)),
            Message m => (m.TimestampUtc, Encoding.UTF8.GetByteCount(m.Content)),
            ReasoningTrace t => (t.StartedAtUtc, Encoding.UTF8.GetByteCount(t.Task + (t.Outcome ?? ""))),
            _ => (null, 0),
        };
    }
}
