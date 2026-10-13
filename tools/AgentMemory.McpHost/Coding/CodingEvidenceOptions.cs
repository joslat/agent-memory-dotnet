namespace AgentMemory.McpHost.Coding;

/// <summary>Optional evidence delivery. Null configuration adds no services, tools, cache or read work.</summary>
internal sealed record CodingEvidenceOptions
{
    public int MaxItems { get; init; } = 16;
    public int MaxBytes { get; init; } = 8192;
    public int DeadlineMilliseconds { get; init; } = 5000;
    public int RetainedPreviews { get; init; } = 128;
    public int LifetimeSeconds { get; init; } = 300;
    public void Validate()
    {
        if (MaxItems is < 1 or > 64 || MaxBytes is < 1024 or > 65536 || DeadlineMilliseconds is < 100 or > 30000)
            throw new ArgumentException("Evidence requires 1..64 items, 1024..65536 UTF-8 response bytes and a 100..30000 ms deadline.");
        new CodingRecallDiagnosticsOptions { RetainedReceipts = RetainedPreviews, LifetimeSeconds = LifetimeSeconds }.Validate();
    }
}

internal sealed record CodingEvidenceSelection
{
    public required string Section { get; init; }
    public required string ItemId { get; init; }
    public string? SourceMessageId { get; init; }
}
internal sealed record CodingEvidencePreviewItem(string Section, string ItemId, IReadOnlyList<string> SourceMessageIds,
    DateTimeOffset? CreatedAtUtc, int EstimatedTextBytes);
internal sealed record CodingEvidencePreview(string PreviewId, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<CodingEvidencePreviewItem> Items, bool Truncated, bool RetrievalTruncated,
    bool LatencyBudgetExceeded, IReadOnlyList<string> OtherContextAvailable);
internal sealed record CodingEvidenceAssertion(string? Commit, string? Source)
{
    public string Verification => "caller_asserted";
}
internal sealed record CodingEvidenceItem(string Section, string ItemId, string? SourceMessageId, string Status,
    string? Text, DateTimeOffset? TimestampUtc, IReadOnlyList<string> SourceMessageIds, CodingEvidenceAssertion? Assertion,
    bool Truncated = false)
{
    public string Trust => "untrusted_memory";
}
internal sealed record CodingEvidenceExpansion(string PreviewId, DateTimeOffset ReadAtUtc, IReadOnlyList<CodingEvidenceItem> Items, bool Truncated);
internal sealed class CodingEvidenceDeadlineException : Exception;
