namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Aggregate usage statistics for a single tool, computed over recorded <see cref="ToolCall"/>s.
/// </summary>
/// <param name="ToolName">The tool these stats are for.</param>
/// <param name="TotalCalls">Total number of recorded calls to the tool.</param>
/// <param name="SuccessfulCalls">Calls whose status is <c>success</c>.</param>
/// <param name="FailedCalls">Calls whose status is <c>error</c>, <c>failure</c>, or <c>timeout</c>.</param>
/// <param name="SuccessRate">Successful ÷ total (0.0 when there are no calls).</param>
/// <param name="AvgDurationMs">Mean recorded duration in milliseconds, or <c>null</c> when there are no calls.</param>
public sealed record ToolCallStats(
    string ToolName,
    int TotalCalls,
    int SuccessfulCalls,
    int FailedCalls,
    double SuccessRate,
    double? AvgDurationMs);
