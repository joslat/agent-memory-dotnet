using AgentMemory.Abstractions.Domain;
using AgentMemory.Gate;

namespace AgentMemory.McpHost.Coding;

internal enum CodingRecallFeedback { Useful, Irrelevant, Stale, Incorrect }
internal sealed record CodingRecallItem(string Section, string ItemId, IReadOnlyList<string> SourceMessageIds);
internal sealed record CodingRecallSection(string Section, int Selected, bool? Searched, int? RequestedLimit, int? Returned, bool? TimedOut);
internal sealed record CodingRecallGate(string Outcome, string? FallbackReason, int? Offered, int? Kept, long? JudgeMilliseconds);
internal sealed record CodingRecallReceipt
{
    public required string RecallId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required long ElapsedMilliseconds { get; init; }
    public required int TotalItemsRetrieved { get; init; }
    public int? EstimatedTokenCount { get; init; }
    public bool Truncated { get; init; }
    public bool LatencyBudgetExceeded { get; init; }
    public bool HasWorkingMemory { get; init; }
    public bool HasGraphRagContext { get; init; }
    public bool IdsTruncated { get; init; }
    public required CodingRecallGate Gate { get; init; }
    public required IReadOnlyList<CodingRecallItem> Items { get; init; }
    public required IReadOnlyList<CodingRecallSection> Sections { get; init; }
    public CodingRecallFeedback? Feedback { get; init; }
    public DateTimeOffset? FeedbackAtUtc { get; init; }

    public static CodingRecallReceipt Create(RecallResult result, DateTimeOffset now, TimeSpan lifetime, TimeSpan elapsed)
    {
        var context = result.Context;
        var items = new List<CodingRecallItem>();
        var sections = new List<CodingRecallSection>();
        var truncated = false;
        // These are the item sections delivered by McpMemoryProjection.Context, not all internal candidates.
        Add("recentMessages", context.RecentMessages, m => m.MessageId, _ => []);
        Add("relevantMessages", context.RelevantMessages, m => m.MessageId, _ => []);
        Add("relevantEntities", context.RelevantEntities, e => e.EntityId, e => e.SourceMessageIds);
        Add("relevantFacts", context.RelevantFacts, f => f.FactId, f => f.SourceMessageIds);
        Add("relevantPreferences", context.RelevantPreferences, p => p.PreferenceId, p => p.SourceMessageIds);
        Add("similarTraces", context.SimilarTraces, t => t.TraceId, _ => []);
        Add("dueFacts", context.DueFacts, f => f.FactId, f => f.SourceMessageIds);
        Add("expiringFacts", context.ExpiringFacts, f => f.FactId, f => f.SourceMessageIds);
        var gate = new CodingRecallGate("unknown", null, null, null, null);
        if (context.Metadata.TryGetValue(MemoryGateTrace.MetadataKey, out var value) && value is MemoryGateTrace trace)
        {
            var outcome = trace.Outcome is "judge" or "floor" or "everything" or "floor (fallback)" ? trace.Outcome : "unknown";
            // FallbackReason may be an arbitrary provider exception. Never retain it or guess its cause.
            var reason = outcome == "floor (fallback)" ?
                trace.FallbackReason == "no judge is configured (MemoryGateOptions.Judges)" ? "not_configured" : "unspecified" : null;
            gate = new(outcome, reason, trace.Offered, trace.Kept, trace.JudgeMilliseconds);
        }
        return new()
        {
            RecallId = Guid.NewGuid().ToString("N"), CreatedAtUtc = now, ExpiresAtUtc = now + lifetime,
            ElapsedMilliseconds = Math.Max(0, (long)elapsed.TotalMilliseconds), TotalItemsRetrieved = result.TotalItemsRetrieved,
            EstimatedTokenCount = result.EstimatedTokenCount, Truncated = result.Truncated,
            LatencyBudgetExceeded = context.LatencyBudgetExceeded, HasWorkingMemory = !string.IsNullOrEmpty(context.WorkingMemoryBlock),
            HasGraphRagContext = !string.IsNullOrEmpty(context.GraphRagContext), IdsTruncated = truncated,
            Gate = gate, Items = items.AsReadOnly(), Sections = sections.AsReadOnly(),
        };

        void Add<T>(string name, MemoryContextSection<T> section, Func<T, string> id, Func<T, IReadOnlyList<string>> sources)
        {
            var d = section.Diagnostics;
            sections.Add(new(name, section.Items.Count, d?.Searched, d?.RequestedLimit, d?.Returned, d?.TimedOut));
            foreach (var item in section.Items)
            {
                var key = id(item);
                if (items.Count >= 64 || !ValidId(key)) { truncated = true; continue; }
                var provenance = sources(item);
                var bounded = provenance.Take(4).Where(ValidId).ToArray();
                if (bounded.Length != provenance.Count) truncated = true;
                items.Add(new(name, key, Array.AsReadOnly(bounded)));
            }
        }
    }
    private static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Any(char.IsControl);
}
