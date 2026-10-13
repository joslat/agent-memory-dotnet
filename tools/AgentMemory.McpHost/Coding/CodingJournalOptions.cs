namespace AgentMemory.McpHost.Coding;

/// <summary>Explicit private receipt journal. Absence registers no persistence service and performs no journal I/O.</summary>
internal sealed record CodingJournalOptions
{
    public required string Directory { get; init; }
    public int RetainedJobs { get; init; } = 512;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory) || !Path.IsPathFullyQualified(Directory) ||
            Path.GetFullPath(Directory).StartsWith("\\\\", StringComparison.Ordinal) ||
            Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(Directory)?.TrimEnd(Path.DirectorySeparatorChar) ||
            RetainedJobs is < 64 or > 4096)
            throw new ArgumentException("Journal requires a dedicated absolute local directory and 64..4096 retained receipts.");
    }
}
