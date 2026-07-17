using System.Diagnostics;
using Microsoft.Extensions.Options;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;

namespace AgentMemory.Nams.Observability;

/// <summary>See <see cref="INamsHealthCheck"/>.</summary>
internal sealed class NamsHealthCheck : INamsHealthCheck
{
    private readonly INamsClient _namsClient;
    private readonly NamsOptions _options;

    public NamsHealthCheck(INamsClient namsClient, IOptions<NamsOptions> options)
    {
        _namsClient = namsClient;
        _options = options.Value;
    }

    public async Task<NamsHealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        // Configuration health: by the time this runs, AddNamsAgentMemory's ValidateOnStart has already
        // guaranteed Endpoint/ApiKey are present -- this is defense-in-depth for a host that catches that
        // startup exception and still exposes health probes, not the primary guarantee.
        if (_options.Endpoint is null || string.IsNullOrWhiteSpace(_options.ApiKey))
            return new NamsHealthCheckResult { Status = NamsHealthStatus.Unhealthy, Description = "NAMS is not configured (missing Endpoint or ApiKey)." };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // The one safe, side-effect-free, no-precondition read this client exposes -- see
            // INamsClient.ListEntitiesAsync's own doc for why SearchEntitiesAsync/GetContextAsync don't work
            // as a probe (a required non-empty query, and a pre-existing conversation, respectively).
            await _namsClient.ListEntitiesAsync(1, cancellationToken).ConfigureAwait(false);
            return new NamsHealthCheckResult
            {
                Status = NamsHealthStatus.Healthy,
                Description = "NAMS is reachable and authenticated.",
                Latency = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancellation -- never reported as a health result
        }
        catch (NamsOperationException ex)
        {
            return new NamsHealthCheckResult { Status = ClassifyFailure(ex.FailureKind), Description = Describe(ex.FailureKind), Latency = stopwatch.Elapsed };
        }
    }

    private static NamsHealthStatus ClassifyFailure(NamsFailureKind failureKind) => failureKind switch
    {
        NamsFailureKind.RateLimited => NamsHealthStatus.Degraded,
        _ => NamsHealthStatus.Unhealthy
    };

    private static string Describe(NamsFailureKind failureKind) => failureKind switch
    {
        NamsFailureKind.Authentication => "NAMS rejected the configured API key.",
        NamsFailureKind.Authorization => "NAMS accepted the API key but denied access to this workspace.",
        NamsFailureKind.RateLimited => "NAMS is currently rate-limiting requests.",
        NamsFailureKind.Network => "NAMS is unreachable (network failure).",
        NamsFailureKind.Timeout => "NAMS did not respond within the configured timeout.",
        _ => $"NAMS returned an unexpected error ({failureKind})."
    };
}
