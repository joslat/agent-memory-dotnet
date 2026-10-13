namespace AgentMemory.McpHost.Coding;

/// <summary>Host-owned persistence boundary; the queue serializes every call under its admission lock.</summary>
internal interface ICodingWriteJournal
{
    IReadOnlyList<CodingJournalEntry> Load();
    void Save(CodingJournalEntry entry);
    void Remove(string jobId);
    bool CanResume(CodingIdentity identity);
}

internal sealed record CodingJournalEntry
{
    public required CodingIdentity Identity { get; init; }
    public required string Key { get; init; }
    public required string Hash { get; init; }
    public CodingRememberRequest? Request { get; init; }
    public required CodingWriteJob Job { get; init; }
}
