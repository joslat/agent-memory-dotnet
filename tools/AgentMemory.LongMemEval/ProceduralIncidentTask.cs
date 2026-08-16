using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 26.1. A <b>second</b> procedural task shape, so the benefit result stops being n=1.
/// </summary>
/// <remarks>
/// <para>
/// The published procedural result is an existence proof on one task and one model. The open question
/// is whether the effect is a property of <i>procedural memory</i> or of <i>that task</i>, and only a
/// second, structurally different task can begin to answer it.
/// </para>
/// <para>
/// <b>It has to satisfy the same four validity rules, which cost seven runs to learn</b> (see
/// <see cref="ProceduralBenchmarkTask"/>):
/// </para>
/// <list type="number">
/// <item>The dependency must not be inferable from tool or parameter names. Here the token that
/// unblocks a republish comes from an <c>artifact registry</c> lookup — a name that suggests
/// inventory, not authorisation.</item>
/// <item>No description may reveal the chain; the <c>ChainRevealingWords</c> guard is asserted against
/// this task too.</item>
/// <item>There must be a convention discoverable <b>only by being refused</b>. Here a change window
/// must be acquired before republishing, and nothing in <c>acquire_change_window</c> connects it to
/// republishing. That is what a runbook exists to record.</item>
/// <item>Refusals must be detectable, so promotion stores the calls that <i>worked</i> rather than the
/// transcript of stumbling into success.</item>
/// </list>
/// <para>
/// <b>What is deliberately different from the rail task.</b> The chain is a different length and a
/// different shape: the gate (<c>AcquireChangeWindow</c>) sits <i>before</i> the payload rather than
/// between two lookups, and the token source is consulted first rather than mid-chain. A second task
/// that merely renamed the first one would measure the same thing twice.
/// </para>
/// </remarks>
internal sealed class ProceduralIncidentTask : IProceduralTask
{
    /// <summary>Marker the agent can only emit by completing the real chain.</summary>
    internal const string ConfirmationMarker = "SERVICE-RESTORED";

    /// <summary>Every refusal starts with this, so "did this call work" is an exact test.</summary>
    internal const string RefusalPrefix = ProceduralBenchmarkTask.RefusalPrefix;

    private const string Service = "checkout-api";
    private const string BadVersion = "4.19.2";
    private const string QuarantineToken = "QTN-8823";
    private const string WindowReference = "CW-3106";

    private bool _windowAcquired;

    public List<string> Calls { get; } = [];

    public string Prompt =>
        $"Service '{Service}' is failing after release {BadVersion}. Restore it. "
        + "Reply with the restoration reference exactly as the tool returns it.";

    public bool IsComplete(string response) =>
        response.Contains(ConfirmationMarker, StringComparison.Ordinal);

    public IReadOnlyList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(InspectArtifactRegistry),
        AIFunctionFactory.Create(AcquireChangeWindow),
        AIFunctionFactory.Create(RepublishPrevious),
        .. Decoys(),
    ];

    [Description("Returns registry entries for a service release.")]
    private string InspectArtifactRegistry(
        [Description("The service name.")] string service)
    {
        Calls.Add(nameof(InspectArtifactRegistry));
        // The quarantine token is buried in an otherwise unremarkable registry listing. Nothing in the
        // tool's name or signature says it is the source of an authorisation token.
        return $"registry for {service}: 4.19.2 (current), 4.19.1 (previous), "
            + $"digest sha256:9f2c, quarantine={QuarantineToken}";
    }

    [Description("Opens a maintenance slot for a service.")]
    private string AcquireChangeWindow(
        [Description("The service name.")] string service)
    {
        Calls.Add(nameof(AcquireChangeWindow));
        _windowAcquired = true;
        return $"window {WindowReference} open for {service}";
    }

    [Description("Republishes the preceding release of a service.")]
    private string RepublishPrevious(
        [Description("The service name.")] string service,
        [Description("The quarantine value.")] string quarantine)
    {
        Calls.Add(nameof(RepublishPrevious));

        // The undocumented convention. Discoverable only by being refused -- nothing in
        // acquire_change_window's name or description connects it to republishing.
        if (!_windowAcquired)
            return $"{RefusalPrefix} no change window is open for {service}.";

        // The non-inferable dependency: the token exists, but only the registry yields it.
        if (!string.Equals(quarantine, QuarantineToken, StringComparison.Ordinal))
            return $"{RefusalPrefix} quarantine value not recognised for {service}.";

        return $"{ConfirmationMarker} {Service}@4.19.1 window={WindowReference}";
    }

    /// <summary>
    /// Plausible tools that are never needed, so calling everything stops being free.
    /// </summary>
    /// <remarks>
    /// Same reason as the rail task: with three real tools an agent skips discovery by invoking all of
    /// them, the unguided policy is already near-optimal, and a stored procedure cannot pay for itself.
    /// Deliberately relevant-sounding — obvious filler is skipped on sight.
    /// </remarks>
    private IEnumerable<AITool> Decoys() =>
        new (string Name, string Description)[]
        {
            ("get_error_rate", "Returns the current error rate for a service."),
            ("list_replicas", "Lists running replicas of a service."),
            ("tail_logs", "Returns recent log lines for a service."),
            ("get_dependency_graph", "Returns upstream and downstream services."),
            ("check_quota", "Returns remaining compute quota for a service."),
            ("list_incidents", "Lists open incidents."),
            ("get_owner", "Returns the owning team for a service."),
            ("check_certificate", "Returns TLS certificate expiry for a service."),
            ("list_feature_flags", "Lists feature flags affecting a service."),
            ("get_latency_percentiles", "Returns latency percentiles for a service."),
            ("list_config_versions", "Lists configuration versions for a service."),
            ("check_disk_usage", "Returns disk usage for a service's nodes."),
        }
        .Select(decoy => AIFunctionFactory.Create(
            (string query) =>
            {
                Calls.Add(decoy.Name);
                return $"{decoy.Name}: no action required for this restoration.";
            },
            decoy.Name,
            decoy.Description));
}
