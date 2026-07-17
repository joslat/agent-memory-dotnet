namespace AgentMemory.Nams.Observability;

/// <summary>
/// The outcome of an <see cref="INamsHealthCheck"/> probe. Deliberately distinguishes <see cref="Unhealthy"/>
/// from <see cref="Degraded"/> (engineering plan Phase 9: "status distinction between unhealthy and
/// degraded/rate-limited") -- rate limiting means the service is reachable and authenticated, just
/// throttled, which calls for a different operational response than an outage or bad credentials.
/// </summary>
public enum NamsHealthStatus
{
    /// <summary>The backend is reachable and authenticated.</summary>
    Healthy,

    /// <summary>The backend is reachable but currently rate-limiting requests.</summary>
    Degraded,

    /// <summary>The backend is unreachable, or credentials are invalid/insufficient.</summary>
    Unhealthy
}
