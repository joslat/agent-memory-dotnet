namespace AgentMemory.Nams.Observability;

/// <summary>
/// A lightweight, host-agnostic health probe for the NAMS backend (engineering plan Phase 9). Deliberately
/// not an ASP.NET Core <c>IHealthCheck</c> -- this package takes no dependency on
/// <c>Microsoft.Extensions.Diagnostics.HealthChecks</c>, so a host that wants one adapts this thin result
/// itself. Never performs a destructive write (plan: "no destructive write probe by default").
/// </summary>
public interface INamsHealthCheck
{
    Task<NamsHealthCheckResult> CheckAsync(CancellationToken cancellationToken);
}
