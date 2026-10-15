namespace AgentMemory.McpHost.Coding;

/// <summary>Opt-in recall receipts. Absence adds no receipt service/tools, telemetry or diagnostic retrieval work.</summary>
internal sealed record CodingRecallDiagnosticsOptions
{
    public int RetainedReceipts { get; init; } = 256;
    public int LifetimeSeconds { get; init; } = 600;
    public void Validate()
    {
        if (RetainedReceipts is < 1 or > 1024 || LifetimeSeconds is < 1 or > 3600)
            throw new ArgumentException("Recall diagnostics require 1..1024 receipts and a 1..3600 second lifetime.");
    }
}
