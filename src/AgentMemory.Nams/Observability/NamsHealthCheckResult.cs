namespace AgentMemory.Nams.Observability;

/// <summary>The result of an <see cref="INamsHealthCheck"/> probe.</summary>
public sealed record NamsHealthCheckResult
{
    public required NamsHealthStatus Status { get; init; }

    /// <summary>A short, human-readable description -- never the raw exception message (which may echo
    /// request/response details); see <see cref="INamsHealthCheck"/> for the redaction rule this follows.</summary>
    public required string Description { get; init; }

    /// <summary>How long the probe took, when it performed a network call (<see langword="null"/> for the
    /// configuration-only check, which never touches the network).</summary>
    public TimeSpan? Latency { get; init; }
}
